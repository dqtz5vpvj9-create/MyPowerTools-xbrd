using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MyPowerTools.Platform.Abstractions;

namespace Xbrd.CodexQuota.Service;

/// <summary>
/// xbrd.codex-quota.service - the single resident publisher of the <c>quota.codex</c>
/// router source. It replaces the three overlapping producers that used to exist
/// outside MyPowerTools (logon-triggered pythonw worker, Codex Stop hook, 5-minute
/// trigger task), so its payload must match
/// <c>esp32-screen/tools/xbrd_codex_quota_plugin.py</c> exactly:
///
/// <list type="bullet">
///   <item>source id <c>quota.codex</c>, panel key <c>codex</c>, label <c>CDX</c>, kind <c>double</c>;</item>
///   <item>live fields <c>h5_left</c> / <c>h5_reset</c> / <c>d7_left</c> / <c>d7_reset</c> in that order,
///         with JSON <c>null</c> for a window Codex did not report (the router stores them as-is);</item>
///   <item><c>changed_fields</c> lists the four fields only on <c>ok</c>; <c>stale</c> and
///         <c>degraded</c> publish an empty <c>changed_fields</c> and an empty <c>panel_patch</c>
///         so the router keeps the last good panel values;</item>
///   <item><c>ttl_s = 300</c> and a <c>codex-</c> prefixed revision sanitised to
///         <c>[A-Za-z0-9_.-]</c> and truncated to 64 characters;</item>
///   <item><c>h5_reset</c> is the UTC+8 wall clock (<c>HH:mm</c>), <c>d7_reset</c> is the remaining
///         duration (<c>0m</c>, <c>2h30</c>, <c>4d00</c>) - same formatters as the old plugin.</item>
/// </list>
///
/// Quota numbers come from <see cref="CodexQuotaReader"/> (app-server
/// <c>account/rateLimits/read</c> with a session-JSONL fallback); no auth file is ever opened,
/// so no token can reach stdout. Both subprocess launches inside the reader already use
/// <c>UseShellExecute=false</c> + <c>CreateNoWindow=true</c>.
///
/// Known deviation, reported to the Lead: the old plugin marked a snapshot <c>stale</c> when the
/// underlying session event was older than <c>ttl_s</c>. <see cref="CodexQuotaSnapshot"/> does not
/// expose the event timestamp, so only the "reset time already passed" half of that check can be
/// reproduced. The age half needs an <c>ObservedAt</c> member on the shared abstraction
/// (MPT main repo, outside this workstream's write scope).
///
/// Command line (the unit manifest passes no arguments):
///   --once       read and publish a single snapshot, then exit (0 ok / 1 failed)
///   --dry-run    print the snapshot JSON on stdout without publishing
/// </summary>
internal static class Program
{
    private const string UnitId = "xbrd.codex-quota.service";
    private const string SourceId = "quota.codex";
    private const string PanelKey = "codex";
    private const string Label = "CDX";
    private const string FieldKind = "double";
    private const string DefaultPublisher = "http://ow.lixinrui000.cn:8080";
    private const string DefaultDisplayTimeZone = "Asia/Shanghai";
    private const string AppServerSourceTag = "app-server-rateLimits-read";
    private const string SessionsSourceTag = "sessions-tail";
    private const string ReadFailedSourceTag = "read-failed";
    private const string NoRecentEventError = "no recent codex rate-limit event";
    private const int DefaultIntervalSeconds = 300;
    private const int TtlSeconds = 300;
    private const int PublishTimeoutSeconds = 8;
    private const int RevisionMaxLength = 64;
    private const int RevisionPrefixLength = 6; // "codex-"

    private static readonly string[] QuotaFieldNames = { "h5_left", "h5_reset", "d7_left", "d7_reset" };
    private static readonly Regex UnsafeRevisionChars = new("[^A-Za-z0-9_.-]+", RegexOptions.Compiled);

    /// <summary>
    /// The legacy python producer wrote the ISO offset as a literal <c>+</c>; the default
    /// System.Text.Json encoder would write <c>\u002B</c>. Both parse identically, but the relaxed
    /// encoder keeps the published bytes the same as the plugin it replaces. Every value written
    /// here is an internally generated number/ASCII string, so nothing user supplied is unescaped.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// A built snapshot plus the metadata the tick loop needs for logging and the heartbeat.
    /// <c>Payload</c> is what gets POSTed and never carries diagnostic-only members.
    /// </summary>
    private sealed record SnapshotBuild(
        JsonObject Payload,
        string Status,
        string Error,
        string Revision,
        string SourceTag,
        string FreshnessLabel,
        Dictionary<string, string?> Fields);

    /// <summary>Result of the v1 session-activity freshness proxy.</summary>
    private sealed record SessionFreshness(bool Applicable, bool Known, bool Stale, DateTimeOffset? NewestUtc)
    {
        public static readonly SessionFreshness NotApplicable = new(false, false, false, null);
        public static readonly SessionFreshness Unknown = new(true, false, false, null);

        public string Label => !Applicable ? "n/a-live-rpc" : !Known ? "unknown" : Stale ? "stale" : "fresh";
    }

    private static async Task<int> Main(string[] args)
    {
        var dryRun = args.Any(static a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        var once = dryRun || args.Any(static a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

        var publisher = (Environment.GetEnvironmentVariable("XBRD_PUBLISHER") ?? DefaultPublisher).Trim().TrimEnd('/');
        var intervalSeconds = Math.Max(5, ReadPositiveInt("XBRD_CODEX_INTERVAL_SECONDS", DefaultIntervalSeconds));
        var dataRoot = ResolveDataRoot();
        Directory.CreateDirectory(dataRoot);
        var heartbeatFile = Path.Combine(dataRoot, UnitId + ".heartbeat");

        if (dryRun)
        {
            var build = await ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(build.Payload.ToJsonString(PayloadJsonOptions));
            return 0;
        }

        // The enableCodexSource setting is not plumbed into the service-unit environment by v1 of
        // the contract, so absence must keep the unit resident (never exit silently).
        var enabled = ReadEnabledFlag("XBRD_CODEX_ENABLED", "XBRD_ENABLE_CODEX_SOURCE");
        if (enabled is null)
        {
            Console.WriteLine(AsciiSafe(
                $"[{UnitId}] settings flag enableCodexSource is absent from the unit environment; defaulting to enabled and staying resident"));
        }
        else if (!enabled.Value)
        {
            Console.WriteLine(AsciiSafe(
                $"[{UnitId}] settings flag enableCodexSource=false; staying resident without publishing"));
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        };

        Console.WriteLine(AsciiSafe(
            $"[{UnitId}] started pid={Environment.ProcessId} publisher={publisher} interval={intervalSeconds}s dataRoot={dataRoot} ttl={TtlSeconds}s"));

        using var http = CreateHttpClient();
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var tick = 0L;
        var consecutiveFailures = 0;
        var lastGoodRevision = "";
        var failedTicks = 0;

        while (!cts.IsCancellationRequested)
        {
            tick++;
            var stopwatch = Stopwatch.StartNew();
            string status;
            string revision = "";
            string error = "";
            string sourceTag = "";
            var freshnessLabel = "-";
            int? httpStatus = null;
            var fields = EmptyQuotaFields();

            if (enabled == false)
            {
                status = "disabled";
            }
            else
            {
                // A Ctrl+C / ProcessExit arriving during the quota read cancels the linked token inside
                // CodexQuotaReader, which surfaces as an OperationCanceledException from the read. It must
                // take the normal shutdown path: letting it escape Main would crash the unit with
                // 0xE0434352 instead of exiting cleanly (verified with a CTRL_BREAK test).
                SnapshotBuild build;
                try
                {
                    build = await ReadSnapshotAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }

                if (cts.IsCancellationRequested)
                {
                    break;
                }

                status = build.Status;
                revision = build.Revision;
                error = build.Error;
                sourceTag = build.SourceTag;
                freshnessLabel = build.FreshnessLabel;
                fields = build.Fields;

                try
                {
                    using var content = new StringContent(
                        build.Payload.ToJsonString(PayloadJsonOptions),
                        Encoding.UTF8,
                        "application/json");
                    using var response = await http
                        .PostAsync($"{publisher}/api/v1/sources/{SourceId}/snapshot", content, cts.Token)
                        .ConfigureAwait(false);
                    httpStatus = (int)response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"publisher returned HTTP {httpStatus}: {Truncate(body, 200)}");
                    }

                    if (BodyRejectsSnapshot(body))
                    {
                        throw new InvalidOperationException(
                            $"publisher rejected the snapshot: {Truncate(body, 200)}");
                    }

                    consecutiveFailures = 0;
                    if (status == "ok")
                    {
                        lastGoodRevision = revision;
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    failedTicks++;
                    status = "publish-error";
                    error = Truncate(ex.Message, 200);
                    revision = lastGoodRevision;
                }
            }

            stopwatch.Stop();
            var heartbeat = new JsonObject
            {
                ["unit"] = UnitId,
                ["pid"] = Environment.ProcessId,
                ["observedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["status"] = status,
                ["source"] = sourceTag,
                ["freshness"] = freshnessLabel,
                ["httpStatus"] = httpStatus,
                ["revision"] = revision,
                ["lastGoodRevision"] = lastGoodRevision,
                ["h5_left"] = fields["h5_left"],
                ["h5_reset"] = fields["h5_reset"],
                ["d7_left"] = fields["d7_left"],
                ["d7_reset"] = fields["d7_reset"],
                ["consecutiveFailures"] = consecutiveFailures,
                ["error"] = error,
                ["publisher"] = publisher,
                ["intervalSeconds"] = intervalSeconds
            };
            try
            {
                await File.WriteAllTextAsync(heartbeatFile, heartbeat.ToJsonString(PayloadJsonOptions), cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                error = Truncate($"heartbeat write failed: {ex.Message}", 200);
            }

            // Exactly one stdout line per tick. Stdout is forced to ASCII: localized exception
            // messages would otherwise be written in the process code page while the ServiceManager
            // decodes the redirected stream with its own encoding, producing mojibake in the logs
            // viewer. The heartbeat file keeps the original UTF-8 text.
            Console.WriteLine(AsciiSafe(
                $"[{UnitId}] tick={tick} status={status} source={(sourceTag.Length == 0 ? "-" : sourceTag)} " +
                $"freshness={freshnessLabel} " +
                $"http={httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
                $"revision={(revision.Length == 0 ? "-" : revision)} " +
                $"h5_left={fields["h5_left"] ?? "-"} h5_reset={fields["h5_reset"] ?? "-"} " +
                $"d7_left={fields["d7_left"] ?? "-"} d7_reset={fields["d7_reset"] ?? "-"} " +
                $"elapsed_ms={stopwatch.ElapsedMilliseconds}" +
                (error.Length == 0 ? "" : $" error={error}")));

            if (once)
            {
                break;
            }

            try
            {
                await Task.Delay(interval, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.WriteLine(AsciiSafe($"[{UnitId}] stopped ticks={tick} failed={failedTicks}"));
        return once && failedTicks > 0 ? 1 : 0;
    }

    private static async Task<SnapshotBuild> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        CodexQuotaSnapshot? read = null;
        string? readError = null;
        try
        {
            read = await CodexQuotaReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            readError = ex.Message;
        }

        // The mtime proxy is only meaningful for the session-JSONL fallback, where the parsed event can
        // be old. The app-server RPC returns live account state by construction, and the old plugin's
        // stale check (event timestamp vs ttl) could never fire on that path either, so requiring recent
        // session activity there would only add a false "degraded" for genuinely fresh data.
        var freshness = read is not null && string.Equals(read.Source, "sessions", StringComparison.Ordinal)
            ? DetectSessionFreshness(observedAt)
            : SessionFreshness.NotApplicable;

        return BuildSnapshot(read, readError, observedAt, freshness);
    }

    /// <summary>
    /// v1 freshness proxy agreed with the Lead: the newest <c>sessions/**/*.jsonl</c> write time is an
    /// upper bound for "when Codex last produced rate-limit telemetry". Unknown (directory missing,
    /// unreadable, or empty) is treated as not stale, never as stale, so it cannot invent a failure.
    /// </summary>
    private static SessionFreshness DetectSessionFreshness(DateTimeOffset observedAt)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        var sessionsRoot = Path.Combine(codexHome, "sessions");
        try
        {
            if (!Directory.Exists(sessionsRoot))
            {
                return SessionFreshness.Unknown;
            }

            var newest = DateTime.MinValue;
            var found = false;
            foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var modified = File.GetLastWriteTimeUtc(file);
                    if (!found || modified > newest)
                    {
                        newest = modified;
                        found = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (!found)
            {
                return SessionFreshness.Unknown;
            }

            var newestUtc = new DateTimeOffset(DateTime.SpecifyKind(newest, DateTimeKind.Utc));
            var stale = observedAt - newestUtc > TimeSpan.FromSeconds(TtlSeconds);
            return new SessionFreshness(Applicable: true, Known: true, Stale: stale, NewestUtc: newestUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SessionFreshness.Unknown;
        }
    }

    private static SnapshotBuild BuildSnapshot(
        CodexQuotaSnapshot? read,
        string? readError,
        DateTimeOffset observedAt,
        SessionFreshness freshness)
    {
        string status;
        string error;
        Dictionary<string, string?> fields;
        var freshnessDegraded = false;

        if (read is null)
        {
            status = "degraded";
            error = Truncate(readError ?? "Codex quota read failed.", 200);
            fields = EmptyQuotaFields();
        }
        else
        {
            var staleReason = StaleReason(read, observedAt);
            if (staleReason.Length > 0)
            {
                status = "stale";
                error = staleReason;
                fields = EmptyQuotaFields();
            }
            else
            {
                fields = CodexQuotaFields(read, observedAt);
                if (!QuotaFieldsComplete(fields))
                {
                    // Same as the old plugin: an incomplete display is degraded and keeps the
                    // router's previous panel values.
                    status = "degraded";
                    error = "Codex quota event did not contain a complete displayable quota";
                    fields = EmptyQuotaFields();
                }
                else if (freshness is { Applicable: true, Stale: true })
                {
                    // Still publish the values so the panel keeps showing them, but report degraded and
                    // mark only the status as changed so a stale reading is never presented as fresh.
                    status = "degraded";
                    error = NoRecentEventError;
                    freshnessDegraded = true;
                }
                else
                {
                    status = "ok";
                    error = "";
                }
            }
        }

        var publishValues = status == "ok" || freshnessDegraded;
        var sourceTag = read is null
            ? ReadFailedSourceTag
            : string.Equals(read.Source, "app-server", StringComparison.Ordinal) ? AppServerSourceTag : SessionsSourceTag;

        var changedFields = new JsonArray();
        if (status == "ok")
        {
            foreach (var name in QuotaFieldNames)
            {
                changedFields.Add($"quota.{PanelKey}.{name}");
            }
        }
        else if (freshnessDegraded)
        {
            changedFields.Add($"quota.{PanelKey}.status");
        }

        var panelPatch = new JsonObject();
        if (publishValues)
        {
            var panel = new JsonObject
            {
                ["label"] = Label,
                ["kind"] = FieldKind
            };
            foreach (var name in QuotaFieldNames)
            {
                panel[name] = fields[name] is null ? null : JsonValue.Create(fields[name]);
            }

            panelPatch["quota"] = new JsonObject { [PanelKey] = panel };
        }

        var payload = new JsonObject
        {
            ["schema"] = "xbrd.source.snapshot.v1",
            ["schema_version"] = 1,
            ["source_id"] = SourceId,
            ["kind"] = "quota",
            ["generated_at"] = observedAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture),
            ["ttl_s"] = TtlSeconds,
            ["status"] = status,
            ["error"] = error,
            ["revision"] = BuildRevision(observedAt, sourceTag, publishValues ? fields : null),
            ["changed_fields"] = changedFields,
            ["panel_patch"] = panelPatch
        };

        return new SnapshotBuild(
            Payload: payload,
            Status: status,
            Error: error,
            Revision: payload["revision"]!.GetValue<string>(),
            SourceTag: sourceTag,
            FreshnessLabel: freshness.Label,
            Fields: fields);
    }

    private static Dictionary<string, string?> EmptyQuotaFields()
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in QuotaFieldNames)
        {
            fields[name] = null;
        }

        return fields;
    }

    private static Dictionary<string, string?> CodexQuotaFields(CodexQuotaSnapshot snapshot, DateTimeOffset observedAt)
    {
        var shortWindow = snapshot.ShortWindow;
        var weeklyWindow = snapshot.WeeklyWindow;
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["h5_left"] = shortWindow is null
                ? null
                : shortWindow.RemainingPercent.ToString(CultureInfo.InvariantCulture) + "%",
            ["h5_reset"] = shortWindow is null ? null : FormatClockReset(shortWindow.ResetsAt),
            ["d7_left"] = weeklyWindow is null
                ? null
                : weeklyWindow.RemainingPercent.ToString(CultureInfo.InvariantCulture) + "%",
            ["d7_reset"] = weeklyWindow is null ? null : FormatDurationReset(weeklyWindow.ResetsAt, observedAt)
        };
    }

    /// <summary>
    /// Port of the old plugin's <c>quota_fields_complete</c>: a half-present pair or a "--"
    /// placeholder makes the whole display incomplete.
    /// </summary>
    private static bool QuotaFieldsComplete(IReadOnlyDictionary<string, string?> fields)
    {
        var completePairs = 0;
        foreach (var (leftKey, resetKey) in new[] { ("h5_left", "h5_reset"), ("d7_left", "d7_reset") })
        {
            var leftPresent = !string.IsNullOrEmpty(fields[leftKey]);
            var rightPresent = !string.IsNullOrEmpty(fields[resetKey]);
            if ((leftPresent || rightPresent) && !(leftPresent && rightPresent))
            {
                return false;
            }

            if (leftPresent && rightPresent)
            {
                if (fields[leftKey] == "--" || fields[resetKey] == "--")
                {
                    return false;
                }

                completePairs++;
            }
        }

        return completePairs > 0;
    }

    /// <summary>Port of the old plugin's <c>stale_reason</c> reset half.</summary>
    private static string StaleReason(CodexQuotaSnapshot snapshot, DateTimeOffset observedAt)
    {
        var resetEpochs = new List<long?>();
        if (snapshot.ShortWindow is not null)
        {
            resetEpochs.Add(snapshot.ShortWindow.ResetsAt?.ToUnixTimeSeconds());
        }

        if (snapshot.WeeklyWindow is not null)
        {
            resetEpochs.Add(snapshot.WeeklyWindow.ResetsAt?.ToUnixTimeSeconds());
        }

        if (resetEpochs.Count == 0)
        {
            return "";
        }

        var nowEpoch = observedAt.ToUnixTimeSeconds();
        return resetEpochs.All(epoch => epoch is not null && epoch.Value <= nowEpoch)
            ? "Codex quota reset time has passed without a newer total event"
            : "";
    }

    /// <summary>Port of the old plugin's <c>format_clock_reset</c> (HH:mm in the display zone).</summary>
    private static string FormatClockReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return "--";
        }

        var timeZoneName = Environment.GetEnvironmentVariable("XBRD_CODEX_QUOTA_TZ");
        if (string.IsNullOrWhiteSpace(timeZoneName))
        {
            timeZoneName = DefaultDisplayTimeZone;
        }

        timeZoneName = timeZoneName.Trim();
        TimeZoneInfo? zone = null;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneName);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        if (zone is null && string.Equals(timeZoneName, DefaultDisplayTimeZone, StringComparison.OrdinalIgnoreCase))
        {
            // These units publish with InvariantGlobalization=true, where named zones are not
            // available. China has no DST, so the fixed +08:00 offset is exactly the wall clock
            // the old plugin produced with ZoneInfo("Asia/Shanghai"). A non-default override that
            // cannot be resolved falls back to local time, like the old plugin did.
            zone = TimeZoneInfo.CreateCustomTimeZone(
                DefaultDisplayTimeZone,
                TimeSpan.FromHours(8),
                DefaultDisplayTimeZone,
                DefaultDisplayTimeZone);
        }

        var local = zone is null
            ? resetsAt.Value.ToLocalTime()
            : TimeZoneInfo.ConvertTime(resetsAt.Value, zone);
        return local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Port of the old plugin's <c>format_duration_reset</c> ("0m" / "2h30" / "4d00").</summary>
    private static string FormatDurationReset(DateTimeOffset? resetsAt, DateTimeOffset observedAt)
    {
        if (resetsAt is null)
        {
            return "--";
        }

        var remainingSeconds = (long)Math.Max(0d, Math.Floor((resetsAt.Value - observedAt).TotalSeconds));
        if (remainingSeconds < 60)
        {
            return "0m";
        }

        if (remainingSeconds < 24 * 3600)
        {
            var hours = remainingSeconds / 3600;
            var minutes = remainingSeconds % 3600 / 60;
            return hours.ToString(CultureInfo.InvariantCulture) + "h" +
                   minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        var days = remainingSeconds / (24 * 3600);
        var remainderHours = remainingSeconds % (24 * 3600) / 3600;
        return days.ToString(CultureInfo.InvariantCulture) + "d" +
               remainderHours.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Port of the old plugin's <c>revision_from_event</c> shape.</summary>
    private static string BuildRevision(
        DateTimeOffset observedAt,
        string sourceTag,
        IReadOnlyDictionary<string, string?>? fields)
    {
        var parts = new List<string>
        {
            observedAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture),
            sourceTag
        };
        if (fields is not null)
        {
            foreach (var name in QuotaFieldNames)
            {
                parts.Add(fields[name] ?? "");
            }
        }

        var safe = UnsafeRevisionChars.Replace(string.Join("|", parts), "-").Trim('-');
        if (safe.Length == 0)
        {
            return $"codex-{observedAt.ToUnixTimeSeconds()}";
        }

        var revision = "codex-" + safe;
        return revision.Length <= RevisionMaxLength ? revision : revision[..RevisionMaxLength];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(PublishTimeoutSeconds) };
        // CONTRACT.md section 3 reserves `routerToken`; the v1 router publish API is
        // unauthenticated so this stays inert today. It is never logged.
        var token = Environment.GetEnvironmentVariable("XBRD_ROUTER_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return client;
    }

    private static bool BodyRejectsSnapshot(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed)
            {
                return false;
            }

            foreach (var name in new[] { "accepted", "ok" })
            {
                if (parsed.TryGetPropertyValue(name, out var node) &&
                    node is JsonValue value &&
                    value.TryGetValue<bool>(out var flag) &&
                    !flag)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return false;
        }
    }

    private static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MPT_TOOL_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "state",
            "tools",
            "xbrd");
    }

    /// <summary>Returns null when the flag is absent (keep running), otherwise its value.</summary>
    private static bool? ReadEnabledFlag(params string[] names)
    {
        foreach (var name in names)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (bool.TryParse(raw.Trim(), out var parsed))
            {
                return parsed;
            }

            if (raw.Trim() is "1" or "yes" or "on")
            {
                return true;
            }

            if (raw.Trim() is "0" or "no" or "off")
            {
                return false;
            }
        }

        return null;
    }

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(
                Environment.GetEnvironmentVariable(name),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) && value > 0
            ? value
            : fallback;

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    /// <summary>Keeps stdout ASCII-only so redirected capture never suffers an encoding mismatch.</summary>
    private static string AsciiSafe(string text)
    {
        if (text.All(static ch => ch is >= ' ' and <= '~'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch is >= ' ' and <= '~' ? ch : '?');
        }

        return builder.ToString();
    }
}
