using System.Globalization;

namespace Xbrd.Surface.Services;

/// <summary>How a source-control call ended, so the UI can show the real reason instead of a generic failure.</summary>
public enum XbrdControlStatus
{
    Ok,
    Failed,
    Unsupported,
    Unreachable,
    Declined
}

/// <summary>
/// Result of one real control action (router control plane or a local unit endpoint).
/// <see cref="ExitCode"/>/<see cref="StdoutTail"/> carry the router's refresh evidence so the row can
/// show what actually ran instead of just re-reading state.
/// </summary>
public sealed record XbrdControlResult(
    string Action,
    string SourceId,
    XbrdControlStatus Status,
    int HttpStatus,
    string Message,
    string Detail,
    long DurationMs,
    int? ExitCode,
    bool? SnapshotUpdated,
    string StdoutTail,
    string Raw)
{
    public static XbrdControlResult Declined(string action, string sourceId, string message) =>
        new(action, sourceId, XbrdControlStatus.Declined, 0, message, "", 0, null, null, "", "");

    public bool Ok => Status == XbrdControlStatus.Ok;

    public bool IsFailure => Status is not XbrdControlStatus.Ok;

    public string StatusToken => Status switch
    {
        XbrdControlStatus.Ok => "running",
        XbrdControlStatus.Declined => "degraded",
        _ => "error"
    };

    public string StatusLabel => Status switch
    {
        XbrdControlStatus.Ok => "成功",
        XbrdControlStatus.Failed => "失败",
        XbrdControlStatus.Unsupported => "不支持",
        XbrdControlStatus.Unreachable => "不可达",
        _ => "已取消"
    };

    /// <summary>Compact evidence line: exit code, duration, snapshot update, then any stdout tail.</summary>
    public string EvidenceText
    {
        get
        {
            if (Status != XbrdControlStatus.Ok && StdoutTail.Length == 0 && ExitCode is null)
            {
                return Message;
            }

            var parts = new List<string>(4);
            if (ExitCode is { } code)
            {
                parts.Add($"exit_code={code}");
            }

            parts.Add($"duration_ms={DurationMs}");
            if (SnapshotUpdated is { } updated)
            {
                parts.Add($"snapshot_updated={(updated ? "true" : "false")}");
            }

            var head = string.Join(" · ", parts);
            var tail = StdoutTail.Trim();
            if (tail.Length > 0)
            {
                head = head.Length > 0 ? $"{head}\n{tail}" : tail;
            }

            if (Message.Length > 0 && Status != XbrdControlStatus.Ok)
            {
                head = $"{Message}\n{head}";
            }

            return head;
        }
    }

    public string HeadlineText => $"{Action}：{StatusLabel}";
}

/// <summary>Result of <c>GET /api/v1/sources/&lt;id&gt;/log</c>.</summary>
public sealed record XbrdSourceLogResult(
    bool Ok,
    string SourceId,
    string LogPath,
    IReadOnlyList<string> Lines,
    bool Truncated,
    string Error,
    int HttpStatus)
{
    public static XbrdSourceLogResult Failed(string sourceId, string error, int httpStatus) =>
        new(false, sourceId, "", [], false, error, httpStatus);

    public string Text => Lines.Count == 0
        ? (Ok ? "（日志为空）" : Error)
        : string.Join("\n", Lines);

    public string MetaText => Ok
        ? $"{Lines.Count} 行{(Truncated ? "（已截断）" : "")} · {LogPath}"
        : Error;
}

/// <summary>Result of <c>POST /publish-now</c> on a local Service Unit control endpoint (C2).</summary>
public sealed record XbrdPublishNowResult(
    bool Ok,
    string SourceId,
    string Revision,
    long DurationMs,
    string Status,
    string Error,
    string Endpoint,
    bool Skipped = false)
{
    public string Text => Ok
        ? Skipped
            ? $"{SourceId} 已排队（unit 正在发布中）：revision={Revision} · status={Status}"
            : $"{SourceId} 已发布：revision={Revision} · {DurationMs}ms · status={Status}"
        : Error;

    public string DetailText => Ok
        ? string.Create(CultureInfo.InvariantCulture, $"duration_ms={DurationMs} · status={Status} · revision={Revision}{(Skipped ? " · skipped=true（与周期 tick 互斥）" : "")}")
        : Error;
}
