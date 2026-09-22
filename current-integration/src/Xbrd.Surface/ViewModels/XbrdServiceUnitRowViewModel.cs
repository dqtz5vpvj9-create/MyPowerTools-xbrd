using System.Globalization;
using System.Windows.Input;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using Xbrd.Surface.Services;

namespace Xbrd.Surface.ViewModels;

/// <summary>
/// One resident collector row (<c>xbrd.mem.service</c> / <c>xbrd.codex-quota.service</c>) with
/// State / PID / Uptime / RestartCount read through <see cref="IServiceUnitClient"/>.
/// </summary>
public sealed class XbrdServiceUnitRowViewModel : MptObservableViewModel
{
    public XbrdServiceUnitRowViewModel(
        string unitId,
        string displayName,
        ServiceUnitSnapshot? snapshot,
        string unavailableReason,
        XbrdSourcesViewModel owner)
    {
        UnitId = unitId;
        DisplayName = displayName;
        Snapshot = snapshot;
        UnavailableReason = unavailableReason;

        RestartCommand = new MptAsyncRelayCommand(
            () => owner.RestartServiceUnitAsync(this),
            operationName: $"xbrd.unit.restart:{unitId}");
        ToggleCommand = new MptAsyncRelayCommand(
            () => owner.ToggleServiceUnitAsync(this),
            operationName: $"xbrd.unit.toggle:{unitId}");
    }

    public string UnitId { get; }

    /// <summary>Compact label for the aggregate line (mem / codex-quota).</summary>
    public string ShortName => UnitId
        .Replace("xbrd.", "", StringComparison.OrdinalIgnoreCase)
        .Replace(".service", "", StringComparison.OrdinalIgnoreCase);

    public string DisplayName { get; }

    public ServiceUnitSnapshot? Snapshot { get; }

    public string UnavailableReason { get; }

    public bool Installed => Snapshot is not null;

    public bool IsRunning => Snapshot?.State is ServiceUnitState.Active or ServiceUnitState.Degraded;

    public XbrdSeverity Severity
    {
        get
        {
            if (Snapshot is null)
            {
                return XbrdSeverity.Degraded;
            }

            var severity = Snapshot.State switch
            {
                ServiceUnitState.Active => XbrdSeverity.Ready,
                ServiceUnitState.Failed => XbrdSeverity.Error,
                _ => XbrdSeverity.Degraded
            };

            return string.IsNullOrWhiteSpace(Snapshot.LastError) ? severity : severity.Worst(XbrdSeverity.Degraded);
        }
    }

    public string PillToken => Severity.ToToken();

    public bool IsReady => Severity == XbrdSeverity.Ready;

    public bool IsDegraded => Severity == XbrdSeverity.Degraded;

    public bool IsError => Severity == XbrdSeverity.Error;

    public string StateLabel => Snapshot is null ? "未注册" : Snapshot.State.ToString().ToLowerInvariant();

    public string PidText => Snapshot?.Pid is int pid && pid > 0
        ? pid.ToString(CultureInfo.InvariantCulture)
        : "—";

    public string UptimeText => Snapshot?.Uptime is { } uptime ? XbrdFormat.Duration((long)uptime.TotalSeconds) : "—";

    public string RestartText => Snapshot is null
        ? "—"
        : $"{Snapshot.RestartCount} 次（上限 {Snapshot.RestartPolicy.MaxRestarts}）";

    public string ReadinessText => Snapshot?.Readiness is { } readiness
        ? readiness.Address is { Length: > 0 } address ? $"{readiness.Kind} · {address}" : readiness.Kind
        : "—";

    public string LastErrorText => Snapshot?.LastError is { Length: > 0 } error ? error : "无";

    public bool HasError => Snapshot?.LastError is { Length: > 0 };

    public bool IsAutostart => Snapshot?.Autostart ?? false;

    public string AutostartText => Snapshot is null ? "—" : Snapshot.Autostart ? "开机自启" : "手动启动";

    public string VersionText => Snapshot?.Version is { Length: > 0 } version ? version : "—";

    public string ToggleButtonText => IsRunning ? "停止" : "启动";

    public string DetailText => Snapshot is null
        ? UnavailableReason
        : $"PID {PidText} · 运行 {UptimeText} · 重启 {RestartText} · {AutostartText} · 版本 {VersionText} · readiness {ReadinessText}";

    public ICommand RestartCommand { get; }

    public ICommand ToggleCommand { get; }
}
