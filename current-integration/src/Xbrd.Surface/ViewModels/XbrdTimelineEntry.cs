using Xbrd.Surface.Services;

namespace Xbrd.Surface.ViewModels;

/// <summary>
/// One diagnostics/timeline line. Entries are derived from the source table (failure → degraded),
/// never from a new backend: CONTRACT.md keeps this Surface read-only against the publisher.
/// </summary>
public sealed record XbrdTimelineEntry(DateTimeOffset Time, string Level, string Message)
{
    public const string LevelInfo = "info";
    public const string LevelAction = "action";
    public const string LevelWarn = "warn";
    public const string LevelError = "error";

    public string TimeText => XbrdFormat.Time(Time);

    /// <summary>Pill class helpers for the timeline rows.</summary>
    public bool IsInfo => Level is not (LevelWarn or LevelError);

    public bool IsWarn => Level == LevelWarn;

    public bool IsError => Level == LevelError;

    public string LevelLabel => Level switch
    {
        LevelError => "失败",
        LevelWarn => "降级",
        LevelAction => "操作",
        _ => "信息"
    };
}
