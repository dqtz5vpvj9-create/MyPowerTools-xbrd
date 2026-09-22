using System.Globalization;
using System.Text.Json.Nodes;

namespace Xbrd.Surface.Services;

/// <summary>Aggregate severity used by the header summary and by every status pill.</summary>
public enum XbrdSeverity
{
    Ready = 0,
    Degraded = 1,
    Error = 2
}

public static class XbrdSeverityExtensions
{
    /// <summary>Token that matches the Shell status-pill classes (running / degraded / error).</summary>
    public static string ToToken(this XbrdSeverity severity) => severity switch
    {
        XbrdSeverity.Ready => "running",
        XbrdSeverity.Degraded => "degraded",
        _ => "error"
    };

    public static string ToLabel(this XbrdSeverity severity) => severity switch
    {
        XbrdSeverity.Ready => "正常",
        XbrdSeverity.Degraded => "降级",
        _ => "异常"
    };

    public static XbrdSeverity Worst(this XbrdSeverity left, XbrdSeverity right) =>
        left >= right ? left : right;
}

/// <summary>
/// Control capabilities a publisher declares for one source (frozen names from CONTRACT §6:
/// <c>refresh</c> / <c>disable</c> / <c>log</c> / <c>delete</c>). The Surface never shows an action
/// the publisher did not declare, and says so when the publisher declares nothing at all.
/// </summary>
public sealed record XbrdCapabilitySet(bool Refresh, bool Disable, bool Delete, bool Log)
{
    public static XbrdCapabilitySet None { get; } = new(false, false, false, false);

    public static XbrdCapabilitySet From(IReadOnlyList<string>? capabilities)
    {
        if (capabilities is null || capabilities.Count == 0)
        {
            return None;
        }

        var set = new HashSet<string>(capabilities, StringComparer.OrdinalIgnoreCase);
        return new XbrdCapabilitySet(set.Contains("refresh"), set.Contains("disable"), set.Contains("delete"), set.Contains("log"));
    }

    public bool Any => Refresh || Disable || Delete || Log;

    public string Text
    {
        get
        {
            var parts = new List<string>(4);
            if (Refresh)
            {
                parts.Add("refresh");
            }

            if (Disable)
            {
                parts.Add("disable/enable");
            }

            if (Log)
            {
                parts.Add("log");
            }

            if (Delete)
            {
                parts.Add("delete");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// One row of <c>GET {publisherUrl}/api/v1/sources</c> (schema <c>xbrd.source.summary.v1</c>).
/// Field names are frozen by CONTRACT.md §6; parsing is tolerant so an older publisher that
/// omits optional fields still renders instead of failing the whole table.
/// </summary>
public sealed record XbrdSourceSummary(
    string SourceId,
    string Kind,
    string Status,
    string EffectiveStatus,
    bool Expired,
    long AgeSeconds,
    long TtlSeconds,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? ReceivedAt,
    string Revision,
    IReadOnlyList<string> ChangedFields,
    string Error,
    bool Enabled = true,
    IReadOnlyList<string>? Capabilities = null,
    bool CapabilitiesDeclared = false)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>Publisher-declared control capabilities of this source (frozen names, CONTRACT §6).</summary>
    public XbrdCapabilitySet CapabilitySet { get; } = XbrdCapabilitySet.From(Capabilities);

    public bool CanRefresh => CapabilitySet.Refresh;

    public bool CanDisable => CapabilitySet.Disable;

    public bool CanDelete => CapabilitySet.Delete;

    public bool CanLog => CapabilitySet.Log;

    public string EnabledText => Enabled ? "已启用" : "已停用";

    /// <summary>Worst-case reading of the publisher's own status + effective_status + expired flag.</summary>
    public XbrdSeverity Severity => !Enabled
        ? XbrdSeverity.Degraded
        : EffectiveStatus switch
        {
            "error" => XbrdSeverity.Error,
            "degraded" => XbrdSeverity.Degraded,
            "stale" => XbrdSeverity.Degraded,
            "disabled" => XbrdSeverity.Degraded,
            "ok" => Expired ? XbrdSeverity.Degraded : XbrdSeverity.Ready,
            _ => string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase)
                ? XbrdSeverity.Error
                : XbrdSeverity.Degraded
        };

    public string PillToken => Severity.ToToken();

    public string StatusLabel
    {
        get
        {
            var effective = string.IsNullOrWhiteSpace(EffectiveStatus) ? "unknown" : EffectiveStatus;
            var status = string.IsNullOrWhiteSpace(Status) ? "unknown" : Status;
            var suffix = Expired ? " · 已过期" : "";
            var stateText = Enabled ? "" : " · 已停用";
            return effective == status ? $"{effective}{suffix}{stateText}" : $"{effective}（{status}）{suffix}{stateText}";
        }
    }

    public long TtlRemainingSeconds => TtlSeconds - AgeSeconds;

    public string TtlText => TtlSeconds <= 0
        ? "未声明 TTL"
        : TtlRemainingSeconds > 0
            ? $"剩余 {XbrdFormat.Duration(TtlRemainingSeconds)}"
            : $"已过期 {XbrdFormat.Duration(-TtlRemainingSeconds)}";

    public string TtlDetailText => TtlSeconds <= 0
        ? $"age {XbrdFormat.Duration(AgeSeconds)}"
        : $"age {XbrdFormat.Duration(AgeSeconds)} · ttl {XbrdFormat.Duration(TtlSeconds)}";

    public string GeneratedText => GeneratedAt is null
        ? "—"
        : GeneratedAt.Value.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public string ReceivedText => ReceivedAt is null
        ? "—"
        : ReceivedAt.Value.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public string AgeText => XbrdFormat.Duration(AgeSeconds);

    public string ErrorText => HasError ? Error : "无";

    public string ChangedFieldsText => ChangedFields.Count == 0
        ? "—"
        : string.Join(", ", ChangedFields);

    public string RevisionText => string.IsNullOrWhiteSpace(Revision) ? "—" : Revision;

    public string KindText => string.IsNullOrWhiteSpace(Kind) ? "unknown" : Kind;

    public bool IsOwnedByThisTool => XbrdSourceOwnership.UnitIdFor(SourceId) is not null;

    public string? OwnerUnitId => XbrdSourceOwnership.UnitIdFor(SourceId);

    public string OwnerText => XbrdSourceOwnership.OwnerTextFor(SourceId);

    /// <summary>What this tool can actually do with the source, given who produces it.</summary>
    public string ManageHint => IsOwnedByThisTool
        ? $"由本工具的 {OwnerUnitId} 发布：立即发布 = 调用该 unit 的 /publish-now；停用/启用 = 停止或启动该 Service Unit。"
        : !CapabilitiesDeclared
            ? "路由器未提供控制面（无 capabilities 字段）：这是旧发布器，只有本地动作可用。"
            : CapabilitySet.Any
                ? $"路由器控制面能力：{CapabilitySet.Text}。停用后发布器拒绝接受新快照且不计入健康统计。"
                : "路由器未对该来源声明控制能力（生产者不在路由器，需在对应发布端处理）。";
}

/// <summary>
/// TTL-safe rendering helpers shared by the source table, the service rows and the timeline.
/// </summary>
public static class XbrdFormat
{
    public static string Duration(long seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds}s");
        }

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d {span.Hours}h");
        }

        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds}s");
    }

    public static string Time(DateTimeOffset? time) => time is null
        ? "—"
        : time.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public static string LongTime(DateTimeOffset? time) => time is null
        ? "—"
        : time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>Which publisher owns which source, and therefore what this Surface may do about it.</summary>
public static class XbrdSourceOwnership
{
    public const string MemServiceUnitId = "xbrd.mem.service";
    public const string CodexQuotaServiceUnitId = "xbrd.codex-quota.service";

    /// <summary>Source ids this tool publishes, mapped to the Service Unit that publishes them.</summary>
    public static string? UnitIdFor(string sourceId) => sourceId switch
    {
        "quota.mem" => MemServiceUnitId,
        "quota.codex" => CodexQuotaServiceUnitId,
        _ => null
    };

    /// <summary>Sources produced by the local UI Quota Worker (managed with local actions only).</summary>
    public static bool IsUiQuotaSource(string sourceId) =>
        sourceId is "quota.proxy" or "quota.mes";

    /// <summary>Human-readable publisher for sources this tool must not touch (CONTRACT.md §6).</summary>
    public static string OwnerTextFor(string sourceId) => sourceId switch
    {
        "quota.proxy" or "quota.mes" => "UI Quota Worker（每日计划任务）",
        "quota.glm" or "quota.deepseek" or "quota.lab" => "路由器 cron",
        "weather.minhang" => "路由器 cron",
        "plan.smoke" => "开发残留来源（路由器）",
        _ => "路由器发布器"
    };
}

/// <summary>Payload of <c>GET {publisherUrl}/api/v1/sources</c> plus the fetch outcome.</summary>
public sealed record XbrdSourcesSnapshot(
    bool Ok,
    string Schema,
    int SchemaVersion,
    int Count,
    int ExpiredCount,
    int UnhealthyCount,
    IReadOnlyList<XbrdSourceSummary> Sources,
    DateTimeOffset FetchedAt,
    string Endpoint,
    string Error,
    string Transport)
{
    public static XbrdSourcesSnapshot Failed(string endpoint, string error, string transport, DateTimeOffset fetchedAt) =>
        new(false, "", 0, 0, 0, 0, [], fetchedAt, endpoint, error, transport);

    public XbrdSeverity Severity
    {
        get
        {
            if (!Ok)
            {
                return XbrdSeverity.Error;
            }

            if (Sources.Count == 0)
            {
                return XbrdSeverity.Degraded;
            }

            var worst = XbrdSeverity.Ready;
            foreach (var source in Sources)
            {
                worst = worst.Worst(source.Severity);
            }

            return worst;
        }
    }

    public string SummaryText => Ok
        ? $"共 {Count} 个来源 · 过期 {ExpiredCount} · 异常 {UnhealthyCount}"
        : "来源清单不可用";

    /// <summary>
    /// False when the publisher answered without any <c>capabilities</c> field — i.e. an older
    /// router build whose actions would 404. The UI then hides router actions and says why.
    /// </summary>
    public bool ControlPlaneAvailable => Ok && Sources.Any(static source => source.CapabilitiesDeclared);

    public int DisabledCount => Sources.Count(static source => !source.Enabled);

    public int RefreshableCount => Sources.Count(static source => source.CanRefresh);

    public string ControlPlaneText => !Ok
        ? "路由器不可达"
        : ControlPlaneAvailable
            ? $"控制面可用 · 可刷新 {RefreshableCount} · 已停用 {DisabledCount}"
            : "旧发布器：无 capabilities 控制面，行内只有本地动作可用";
}

/// <summary>Payload of <c>GET {publisherUrl}/health</c> plus the probe outcome.</summary>
public sealed record XbrdHealthSnapshot(
    bool Ok,
    bool PanelOk,
    string Service,
    string Host,
    DateTimeOffset? Time,
    long UptimeSeconds,
    int SourceCount,
    int ExpiredCount,
    int UnhealthyCount,
    string PanelSource,
    string PanelUpdated,
    IReadOnlyList<string> PanelErrors,
    IReadOnlyList<string> PanelWarnings,
    string Endpoint,
    string Error)
{
    public static XbrdHealthSnapshot Failed(string endpoint, string error) =>
        new(false, false, "", "", null, 0, 0, 0, 0, "", "", [], [], endpoint, error);

    /// <summary>Panel reachability: reachable + panel_ok, degraded when the panel itself reports problems.</summary>
    public XbrdSeverity Severity
    {
        get
        {
            if (!Ok)
            {
                return XbrdSeverity.Error;
            }

            return PanelOk && PanelErrors.Count == 0
                ? XbrdSeverity.Ready
                : XbrdSeverity.Degraded;
        }
    }

    public string StatusLabel
    {
        get
        {
            if (!Ok)
            {
                return "不可达";
            }

            if (!PanelOk)
            {
                return "发布器在线 · 面板异常";
            }

            return PanelErrors.Count == 0 ? "在线" : "在线 · 有面板错误";
        }
    }

    public string DetailText
    {
        get
        {
            if (!Ok)
            {
                return Error;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Host))
            {
                parts.Add(Host);
            }

            if (!string.IsNullOrWhiteSpace(Service))
            {
                parts.Add(Service);
            }

            parts.Add($"uptime {XbrdFormat.Duration(UptimeSeconds)}");
            if (!string.IsNullOrWhiteSpace(PanelUpdated))
            {
                parts.Add($"panel {PanelUpdated}");
            }

            if (PanelErrors.Count > 0)
            {
                parts.Add($"errors: {string.Join("; ", PanelErrors)}");
            }

            if (PanelWarnings.Count > 0)
            {
                parts.Add($"warnings: {string.Join("; ", PanelWarnings)}");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Small tolerant readers for the publisher's JSON (never throws on shape drift).</summary>
internal static class XbrdJson
{
    public static JsonObject? AsObject(JsonNode? node) => node as JsonObject;

    public static string ReadString(JsonObject? source, string key, string fallback = "")
    {
        try
        {
            var value = source?[key];
            if (value is null)
            {
                return fallback;
            }

            return value.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? value.GetValue<string>()
                : value.ToJsonString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NotSupportedException)
        {
            return fallback;
        }
    }

    public static long ReadLong(JsonObject? source, string key, long fallback = 0)
    {
        try
        {
            var value = source?[key];
            if (value is null)
            {
                return fallback;
            }

            if (value.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                return value.GetValue<long>();
            }

            return long.TryParse(ReadString(source, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    public static int ReadInt(JsonObject? source, string key, int fallback = 0) =>
        (int)Math.Clamp(ReadLong(source, key, fallback), int.MinValue, int.MaxValue);

    public static bool ReadBool(JsonObject? source, string key, bool fallback = false)
    {
        try
        {
            var value = source?[key];
            if (value is null)
            {
                return fallback;
            }

            if (value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                return value.GetValue<bool>();
            }

            return bool.TryParse(ReadString(source, key), out var parsed) ? parsed : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    public static DateTimeOffset? ReadTime(JsonObject? source, string key)
    {
        var raw = ReadString(source, key);
        if (raw.Length == 0)
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    public static IReadOnlyList<string> ReadStringArray(JsonObject? source, string key)
    {
        if (source?[key] is not JsonArray array)
        {
            return [];
        }

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            try
            {
                var text = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                // Ignore non-string entries; the summary stays readable.
            }
        }

        return result;
    }
}
