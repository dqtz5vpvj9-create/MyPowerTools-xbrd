using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Xbrd.Mem.Service;

/// <summary>
/// xbrd.mem.service - resident publisher of the <c>quota.mem</c> router source.
///
/// Behaviour is equivalent to <c>esp32-screen/tools/xbrd_windows_mem_source.ps1</c>:
/// same source id (<c>quota.mem</c>), same panel key (<c>mem</c>), same panel fields
/// (label/kind/left/aux/limit/status), same OK/W/L thresholds on the system commit
/// ratio (85 % / 95 %), same <c>mem-&lt;physical&gt;-&lt;committed&gt;-&lt;limit&gt;-&lt;status&gt;</c>
/// revision shape and the same <c>ttl_s</c> (300). The difference is that this
/// process stays resident and publishes every <c>XBRD_MEM_INTERVAL_SECONDS</c>
/// instead of starting a fresh wscript/powershell process on every tick.
///
/// Equivalence of the memory numbers: <see cref="GlobalMemoryStatusEx"/> reports the
/// system commit limit in <c>ullTotalPageFile</c> and the available commit in
/// <c>ullAvailPageFile</c>. Measured on this host against
/// <c>Win32_PerfFormattedData_PerfOS_Memory</c> (the counters the old script used)
/// the limit matches <c>CommitLimit</c> exactly and the used commit tracks
/// <c>CommittedBytes</c> within sampling skew (&lt; 0.1 GB), so the commit ratio is
/// the same quantity the old script published.
///
/// Failure never overwrites last-good: when collection or the POST fails nothing is
/// published, so the router keeps the previous snapshot until its TTL expires. Only
/// the local heartbeat file records the failure.
///
/// Command line (the unit manifest passes no arguments):
///   --once       publish a single snapshot and exit (0 ok / 1 failed)
///   --dry-run    print the snapshot JSON on stdout without publishing
/// </summary>
internal static class Program
{
    private const string UnitId = "xbrd.mem.service";
    private const string SourceId = "quota.mem";
    private const string PanelKey = "mem";
    private const string DefaultPublisher = "http://ow.lixinrui000.cn:8080";
    private const int DefaultIntervalSeconds = 300;
    private const int TtlSeconds = 300;
    private const int PublishTimeoutSeconds = 8;
    private const double WarnCommitRatio = 0.85;
    private const double LimitCommitRatio = 0.95;

    private static readonly Regex UnsafeRevisionChars = new("[^A-Za-z0-9_.-]+", RegexOptions.Compiled);
    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static async Task<int> Main(string[] args)
    {
        var dryRun = args.Any(static a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        var once = dryRun || args.Any(static a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

        var publisher = (Environment.GetEnvironmentVariable("XBRD_PUBLISHER") ?? DefaultPublisher).Trim().TrimEnd('/');
        var intervalSeconds = Math.Max(5, ReadPositiveInt("XBRD_MEM_INTERVAL_SECONDS", DefaultIntervalSeconds));
        var dataRoot = ResolveDataRoot();
        Directory.CreateDirectory(dataRoot);
        var heartbeatFile = Path.Combine(dataRoot, UnitId + ".heartbeat");
        var platformHeartbeatFile = ResolvePlatformHeartbeatPath(args);

        if (dryRun)
        {
            // Diagnostic path: identical payload shape, no POST, no heartbeat.
            Console.WriteLine(BuildSnapshot().ToJsonString());
            return 0;
        }

        // The enableMemSource setting is not plumbed into the service-unit environment by
        // v1 of the contract, so absence must keep the unit resident (never exit silently).
        var enabled = ReadEnabledFlag("XBRD_MEM_ENABLED", "XBRD_ENABLE_MEM_SOURCE");
        if (enabled is null)
        {
            Console.WriteLine(AsciiSafe(
                $"[{UnitId}] settings flag enableMemSource is absent from the unit environment; defaulting to enabled and staying resident"));
        }
        else if (!enabled.Value)
        {
            Console.WriteLine(AsciiSafe(
                $"[{UnitId}] settings flag enableMemSource=false; staying resident without publishing"));
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
            var observedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            string status;
            string revision = "";
            string error = "";
            int? httpStatus = null;

            if (enabled == false)
            {
                status = "disabled";
            }
            else
            {
                try
                {
                    var snapshot = BuildSnapshot();
                    revision = snapshot["revision"]!.GetValue<string>();
                    using var content = new StringContent(snapshot.ToJsonString(), Encoding.UTF8, "application/json");
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

                    status = "ok";
                    lastGoodRevision = revision;
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never publish on failure: the router keeps last-good until TTL expiry.
                    consecutiveFailures++;
                    failedTicks++;
                    status = "error";
                    error = Truncate(ex.Message, 200);
                    revision = lastGoodRevision;
                }
            }

            stopwatch.Stop();
            var heartbeat = new JsonObject
            {
                ["unit"] = UnitId,
                ["pid"] = Environment.ProcessId,
                ["observedAt"] = Iso(observedAt),
                ["status"] = status,
                ["httpStatus"] = httpStatus,
                ["revision"] = revision,
                ["lastGoodRevision"] = lastGoodRevision,
                ["consecutiveFailures"] = consecutiveFailures,
                ["error"] = error,
                ["publisher"] = publisher,
                ["intervalSeconds"] = intervalSeconds
            };
            try
            {
                await File.WriteAllTextAsync(heartbeatFile, heartbeat.ToJsonString(), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                error = Truncate($"heartbeat write failed: {ex.Message}", 200);
            }

            // Platform liveness heartbeat, separate from the JSON diagnostic above (see CONTRACT.md
            // section 5). Failure is reported on stderr only and never perturbs the publish flow.
            WritePlatformHeartbeat(
                platformHeartbeatFile,
                $"{Iso(observedAt)} {UnitId} tick={tick} status={status} pid={Environment.ProcessId}");

            // Exactly one stdout line per tick. Stdout is forced to ASCII: localized exception
            // messages (and any non-ASCII data root) would otherwise be written in the process
            // code page while the ServiceManager decodes the redirected stream with its own
            // encoding, producing mojibake in the logs viewer. The heartbeat file keeps the
            // original UTF-8 text.
            Console.WriteLine(AsciiSafe(
                $"[{UnitId}] tick={tick} status={status} http={httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
                $"revision={(revision.Length == 0 ? "-" : revision)} elapsed_ms={stopwatch.ElapsedMilliseconds}" +
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

    /// <summary>
    /// Builds the <c>quota.mem</c> snapshot with exactly the field set, ordering,
    /// thresholds and revision shape of the old PowerShell source script.
    /// </summary>
    private static JsonObject BuildSnapshot()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException(
                $"GlobalMemoryStatusEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        const double gb = 1024d * 1024d * 1024d;
        // Double arithmetic on purpose: the old script clamps negative/NaN/Inf to 0.0 instead
        // of wrapping around like unsigned subtraction would.
        var physicalUsedGb = ClampBytes((double)status.ullTotalPhys - status.ullAvailPhys, gb);
        var commitUsedGb = ClampBytes((double)status.ullTotalPageFile - status.ullAvailPageFile, gb);
        var commitLimitGb = ClampBytes(status.ullTotalPageFile, gb);

        var commitRatio = commitLimitGb > 0 ? commitUsedGb / commitLimitGb : 0d;
        var quotaStatus = commitRatio >= LimitCommitRatio ? "L" : commitRatio >= WarnCommitRatio ? "W" : "OK";

        var physicalText = FormatGb(physicalUsedGb);
        var committedText = FormatGb(commitUsedGb);
        var limitText = FormatGb(commitLimitGb);
        var revision = SanitizeRevision($"mem-{physicalText}-{committedText}-{limitText}-{quotaStatus}");

        return new JsonObject
        {
            ["schema"] = "xbrd.source.snapshot.v1",
            ["schema_version"] = 1,
            ["source_id"] = SourceId,
            ["kind"] = "quota",
            ["generated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["ttl_s"] = TtlSeconds,
            ["status"] = "ok",
            ["error"] = "",
            ["revision"] = revision,
            ["changed_fields"] = new JsonArray(
                $"quota.{PanelKey}.left",
                $"quota.{PanelKey}.aux",
                $"quota.{PanelKey}.limit",
                $"quota.{PanelKey}.status"),
            ["panel_patch"] = new JsonObject
            {
                ["quota"] = new JsonObject
                {
                    [PanelKey] = new JsonObject
                    {
                        ["label"] = "MEM",
                        ["kind"] = "simple",
                        ["left"] = physicalText,
                        ["aux"] = committedText,
                        ["limit"] = limitText,
                        ["status"] = quotaStatus
                    }
                }
            }
        };
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

    private static double ClampBytes(double bytes, double divisor)
    {
        if (double.IsNaN(bytes) || double.IsInfinity(bytes) || bytes < 0)
        {
            return 0d;
        }

        return bytes / divisor;
    }

    private static string FormatGb(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string SanitizeRevision(string text)
    {
        var safe = UnsafeRevisionChars.Replace(text, "-").Trim('-');
        return safe.Length == 0 ? $"mem-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" : safe;
    }

    private static bool BodyRejectsSnapshot(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            var parsed = JsonNode.Parse(body);
            return parsed is JsonObject obj &&
                   obj.TryGetPropertyValue("accepted", out var accepted) &&
                   accepted is JsonValue value &&
                   value.TryGetValue<bool>(out var flag) &&
                   !flag;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Platform liveness heartbeat path, i.e. <c>&lt;MPT_DATA_ROOT&gt;\state\&lt;unitId&gt;.heartbeat</c>.
    ///
    /// <c>scripts/configure-user-services.ps1</c> injects <c>MPT_DATA_ROOT</c> into every unit and
    /// rewrites a declared <c>--heartbeat-file</c> argument to that path;
    /// <c>scripts/verify-release-candidate.remote.ps1</c> then installs into an isolated temp data
    /// root and only checks <c>Test-Path "$dataRoot\state\$unitId.heartbeat"</c>. Following
    /// <c>MPT_DATA_ROOT</c> (instead of hard-coding <c>%LOCALAPPDATA%\MyPowerTools</c>) is what makes
    /// that gate pass; the fallback keeps manual runs on the same path as a normal install.
    /// </summary>
    private static string ResolvePlatformHeartbeatPath(string[] args)
    {
        var explicitPath = ReadOption(args, "--heartbeat-file");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath.Trim();
        }

        var platformRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(platformRoot))
        {
            platformRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools");
        }

        return Path.Combine(platformRoot.Trim(), "state", UnitId + ".heartbeat");
    }

    /// <summary>
    /// Overwrites a tiny single-line heartbeat. Deliberately not an append-only log: the release gate
    /// only tests for existence, so the file must stay small no matter how long the unit runs.
    /// </summary>
    private static void WritePlatformHeartbeat(string path, string summary)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, summary + Environment.NewLine, NoBomUtf8);
        }
        catch (Exception ex)
        {
            // Never let the liveness file affect publishing; stderr keeps stdout at one line per tick.
            Console.Error.WriteLine(AsciiSafe($"[{UnitId}] platform heartbeat write failed ({path}): {ex.Message}"));
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < args.Length ? args[index + 1] : null;
            }

            var prefix = name + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
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

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
