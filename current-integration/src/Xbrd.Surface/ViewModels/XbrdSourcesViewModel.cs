using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using Xbrd.Surface.Services;

namespace Xbrd.Surface.ViewModels;

/// <summary>
/// View model behind the 「来源与配额」 dotnet Surface (route <c>sources</c>).
///
/// What it renders, in contract order (CONTRACT.md §3/§4/§6, task-2):
///  · top aggregate: panel reachability + source health + both Service Units, worst-of;
///  · the source table read from <c>GET {publisherUrl}/api/v1/sources</c>;
///  · collector / Service Unit status including the daily UI Quota scheduled task;
///  · local-only actions that HTTP commands cannot express (collect, open verification window,
///    restart units, logs);
///  · a derived failure → retry → degradation timeline.
///
/// All status text is Chinese to match the rest of the tool; all colours/spacing come from Shell
/// design tokens in the view.
/// </summary>
public sealed class XbrdSourcesViewModel : MptObservableViewModel, IDisposable
{
    private static readonly string[] CollectServiceUnitIds =
    [
        XbrdSourceOwnership.MemServiceUnitId,
        XbrdSourceOwnership.CodexQuotaServiceUnitId
    ];

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly XbrdLocalToolbox _local = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<XbrdTimelineEntry> _sessionEntries = [];
    private XbrdPublisherClient? _publisher;
    private XbrdSurfaceSettings _settings = XbrdSurfaceSettings.Fallback("not initialised");
    private XbrdSourcesSnapshot _sources = XbrdSourcesSnapshot.Failed("", "尚未读取。", "none", DateTimeOffset.Now);
    private XbrdHealthSnapshot _health = XbrdHealthSnapshot.Failed("", "尚未探测。");
    private DispatcherTimer? _autoRefreshTimer;
    private bool _isRefreshing;
    private bool _isBusy;
    private bool _isActionRunning;
    private string _busyText = "正在读取发布器与 Service Unit…";
    private string _selectedSourceId = "";
    private string _snapshotFocusText = "";
    private string _lastActionTitle = "";
    private string _lastActionResult = "";
    private string _unitLogText = "（尚未读取日志）";
    private string _lastUpdatedText = "—";
    private XbrdPanelSnapshot _panelPreview = XbrdPanelSnapshot.Empty;
    private bool _disposed;

    public XbrdSourcesViewModel(MptAvaloniaSurfaceContext context)
    {
        _context = context;

        RefreshCommand = new MptAsyncRelayCommand(
            () => RefreshAsync(userInitiated: true),
            () => !_isRefreshing,
            "xbrd.sources.refresh");
        CollectUiQuotaCommand = new MptAsyncRelayCommand(CollectUiQuotaAsync, () => !_isActionRunning, "xbrd.ui-quota.collect");
        OpenQuotaWindowCommand = new MptAsyncRelayCommand(OpenQuotaWindowAsync, () => !_isActionRunning, "xbrd.ui-quota.edge");
        RestartUnitsCommand = new MptAsyncRelayCommand(RestartAllUnitsAsync, () => !_isActionRunning, "xbrd.units.restart-all");
        RefreshLogsCommand = new MptAsyncRelayCommand(RefreshLogsAsync, () => !_isActionRunning, "xbrd.logs.refresh");
        OpenLogsFolderCommand = new MptAsyncRelayCommand(OpenLogsFolderAsync, () => !_isActionRunning, "xbrd.logs.folder");
    }

    // ---------------------------------------------------------------- headline / aggregate

    public string AggregateHeadline => AggregateSeverity.ToLabel();

    public string AggregatePillToken => AggregateSeverity.ToToken();

    public XbrdSeverity AggregateSeverity =>
        _health.Severity.Worst(_sources.Severity).Worst(UnitsSeverity);

    public string AggregateDetail =>
        $"面板 {_health.StatusLabel} · 来源 {_sources.SummaryText} · 采集服务 {UnitsSummaryText}";

    public bool AggregateIsReady => AggregateSeverity == XbrdSeverity.Ready;

    public bool AggregateIsDegraded => AggregateSeverity == XbrdSeverity.Degraded;

    public bool AggregateIsError => AggregateSeverity == XbrdSeverity.Error;

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    // ---------------------------------------------------------------- panel reachability

    public string PanelPillToken => _health.Severity.ToToken();

    public string PanelLabel => _health.StatusLabel;

    public string PanelDetail => _health.DetailText;

    public string PanelEndpoint => _health.Endpoint.Length == 0 ? _settings.PublisherUrl : _health.Endpoint;

    public bool PanelIsReady => _health.Severity == XbrdSeverity.Ready;

    public bool PanelIsDegraded => _health.Severity == XbrdSeverity.Degraded;

    public bool PanelIsError => _health.Severity == XbrdSeverity.Error;

    public string PanelCountersText => _health.Ok
        ? $"来源 {_health.SourceCount} · 过期 {_health.ExpiredCount} · 异常 {_health.UnhealthyCount}"
        : "—";

    // ---------------------------------------------------------------- sources

    public ObservableCollection<XbrdSourceRowViewModel> Sources { get; } = [];

    public string SourcesPillToken => _sources.Severity.ToToken();

    public string SourcesLabel => _sources.Ok ? _sources.Severity.ToLabel() : "不可用";

    public bool SourcesIsReady => _sources.Severity == XbrdSeverity.Ready;

    public bool SourcesIsDegraded => _sources.Severity == XbrdSeverity.Degraded;

    public bool SourcesIsError => _sources.Severity == XbrdSeverity.Error;

    public string SourcesSummaryText => _sources.SummaryText;

    public bool HasSourcesError => !_sources.Ok;

    public string SourcesErrorText => _sources.Ok
        ? ""
        : $"{_sources.Error}（已尝试 {_sources.Transport}；端点 {_sources.Endpoint}）";

    public bool IsSourcesEmpty => _sources.Ok && Sources.Count == 0;

    public string SourcesMetaText => _sources.Ok
        ? $"schema {_sources.Schema} v{_sources.SchemaVersion} · {_sources.Transport} · 读取于 {XbrdFormat.Time(_sources.FetchedAt)}"
        : $"读取失败 · {XbrdFormat.Time(_sources.FetchedAt)}";

    public string SnapshotFocusText
    {
        get => _snapshotFocusText;
        private set => SetProperty(ref _snapshotFocusText, value);
    }

    // ---------------------------------------------------------------- panel preview

    /// <summary>
    /// The router serves JSON (there is no HTML admin page), so the panel's real content is
    /// previewed here from <c>GET {publisherUrl}/panel.json</c>. Read-only.
    /// </summary>
    public ObservableCollection<XbrdPanelField> PanelStatusFields { get; } = [];

    public ObservableCollection<XbrdPanelField> PanelWeatherFields { get; } = [];

    public ObservableCollection<XbrdPanelField> PanelPlanFields { get; } = [];

    public ObservableCollection<XbrdPanelQuotaRow> PanelQuotaRows { get; } = [];

    public string PanelPreviewPillToken => _panelPreview.PillToken;

    public string PanelPreviewLabel => _panelPreview.Ok ? _panelPreview.StatusSeverity.ToLabel() : "不可用";

    public bool PanelPreviewIsReady => _panelPreview.Ok && _panelPreview.IsReady;

    public bool PanelPreviewIsDegraded => _panelPreview.Ok && _panelPreview.IsDegraded;

    public bool PanelPreviewIsError => !_panelPreview.Ok || _panelPreview.IsError;

    public bool HasPanelPreviewError => _panelPreview.HasError;

    public string PanelPreviewErrorText => _panelPreview.ErrorText;

    public string PanelPreviewUpdatedText => _panelPreview.UpdatedText;

    public string PanelPreviewEndpoint => _panelPreview.Endpoint;

    public string PanelPreviewMetaText => _panelPreview.Ok
        ? $"{_panelPreview.SchemaText} · {_panelPreview.CountsText} · 读取于 {XbrdFormat.Time(_panelPreview.FetchedAt)}"
        : $"读取失败 · {XbrdFormat.Time(_panelPreview.FetchedAt)}";

    public bool HasPanelStatus => PanelStatusFields.Count > 0;

    public bool HasPanelWeather => PanelWeatherFields.Count > 0;

    public bool HasPanelPlan => PanelPlanFields.Count > 0;

    public bool HasPanelQuota => PanelQuotaRows.Count > 0;

    public bool HasPanelPlanTodos => _panelPreview.TodosText.Length > 0;

    public string PanelPlanTodosText => _panelPreview.TodosText;

    // ---------------------------------------------------------------- collectors / units

    public ObservableCollection<XbrdServiceUnitRowViewModel> ServiceUnits { get; } = [];

    public bool HasServiceUnitsError => _serviceUnitsError.Length > 0;

    public string ServiceUnitsErrorText => _serviceUnitsError;

    public XbrdSeverity UnitsSeverity
    {
        get
        {
            if (_serviceUnitsError.Length > 0)
            {
                return XbrdSeverity.Degraded;
            }

            var worst = XbrdSeverity.Ready;
            foreach (var unit in ServiceUnits)
            {
                worst = worst.Worst(unit.Severity);
            }

            return worst;
        }
    }

    public string UnitsPillToken => UnitsSeverity.ToToken();

    public bool UnitsIsReady => UnitsSeverity == XbrdSeverity.Ready;

    public bool UnitsIsDegraded => UnitsSeverity == XbrdSeverity.Degraded;

    public bool UnitsIsError => UnitsSeverity == XbrdSeverity.Error;

    public string UnitsLabel => _serviceUnitsError.Length > 0 ? "宿主不可用" : UnitsSeverity.ToLabel();

    public string UnitsSummaryText => _serviceUnitsError.Length > 0
        ? "服务管理器不可用"
        : ServiceUnits.Count == 0
            ? "未注册"
            : string.Join(" · ", ServiceUnits.Select(unit => $"{unit.ShortName} {unit.StateLabel}"));

    public string UnitsDetail => _serviceUnitsError.Length > 0
        ? _serviceUnitsError
        : "通过 IServiceUnitClient（toolId=xbrd 作用域）读取 State/PID/Uptime/RestartCount。";

    private string _serviceUnitsError = "";

    public string ServiceUnitsError
    {
        get => _serviceUnitsError;
        private set
        {
            if (SetProperty(ref _serviceUnitsError, value))
            {
                OnPropertyChanged(nameof(HasServiceUnitsError));
                OnPropertyChanged(nameof(ServiceUnitsErrorText));
                OnPropertyChanged(nameof(UnitsPillToken));
                OnPropertyChanged(nameof(UnitsLabel));
                OnPropertyChanged(nameof(UnitsSummaryText));
                OnPropertyChanged(nameof(UnitsDetail));
                RaiseAggregate();
            }
        }
    }

    public XbrdUiQuotaTaskStatus UiQuotaTask { get; private set; } =
        XbrdUiQuotaTaskStatus.Failed("尚未查询。", []);

    public string UiQuotaTaskName => XbrdUiQuotaTaskStatus.TaskPathAndName;

    public string CollectorPathText => _local.CollectorPathText;

    public string WorkerLogDirectoryText => _local.WorkerLogDirectory;

    // ---------------------------------------------------------------- local actions

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(WorkingText));
            }
        }
    }

    public bool IsActionRunning
    {
        get => _isActionRunning;
        private set
        {
            if (SetProperty(ref _isActionRunning, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(WorkingText));
            }
        }
    }

    /// <summary>True while any asynchronous work (refresh or local action) is in flight.</summary>
    public bool IsWorking => _isBusy || _isActionRunning;

    public string WorkingText => _isActionRunning ? "正在执行本机动作…" : _busyText;

    public string BusyText
    {
        get => _busyText;
        private set
        {
            if (SetProperty(ref _busyText, value))
            {
                OnPropertyChanged(nameof(WorkingText));
            }
        }
    }

    public string LastActionTitle
    {
        get => _lastActionTitle;
        private set => SetProperty(ref _lastActionTitle, value);
    }

    public string LastActionResult
    {
        get => _lastActionResult;
        private set
        {
            if (SetProperty(ref _lastActionResult, value))
            {
                OnPropertyChanged(nameof(HasLastActionResult));
            }
        }
    }

    public bool HasLastActionResult => _lastActionResult.Length > 0;

    public string UnitLogText
    {
        get => _unitLogText;
        private set => SetProperty(ref _unitLogText, value);
    }

    // ---------------------------------------------------------------- settings diagnostics

    public string SettingsDiagnosticsText => _settings.DiagnosticsText;

    public bool ShowsSettingsWarning => _settings.UsesContractDefaults;

    public string SettingsWarningText =>
        "未能从宿主读取工具设置，已按 CONTRACT.md §3 使用契约默认值；脚本路径与发布器地址可能不正确。";

    public string PublisherUrlText => _settings.PublisherUrl;

    public string PanelUrlText => _settings.PanelUrl;

    public string RepoRootText => _settings.RepoRootText;

    public string TimeoutText => $"{_settings.ConnectionTimeoutMs} ms";

    public string IntervalText => $"MEM {_settings.MemIntervalSeconds}s · Codex {_settings.CodexIntervalSeconds}s";

    public string AutoRefreshText => _settings.AutoRefresh ? "开启（60s）" : "关闭";

    public string SourcesEnabledText =>
        $"MEM 来源 {(_settings.EnableMemSource ? "启用" : "禁用")} · Codex 来源 {(_settings.EnableCodexSource ? "启用" : "禁用")}";

    public string SecretsText => _settings.SecretsText;

    // ---------------------------------------------------------------- timeline

    public ObservableCollection<XbrdTimelineEntry> Timeline { get; } = [];

    public bool IsTimelineEmpty => Timeline.Count == 0;

    // ---------------------------------------------------------------- commands

    public MptAsyncRelayCommand RefreshCommand { get; }

    public ICommand CollectUiQuotaCommand { get; }

    public ICommand OpenQuotaWindowCommand { get; }

    public ICommand RestartUnitsCommand { get; }

    public ICommand RefreshLogsCommand { get; }

    public ICommand OpenLogsFolderCommand { get; }

    // ---------------------------------------------------------------- lifecycle

    public async Task InitializeAsync()
    {
        // Settings are best-effort: an unreadable host degrades to the contract defaults and is
        // flagged in the UI, it must never leave the table empty or throw out of the surface.
        try
        {
            _settings = await XbrdSettingsLoader.LoadAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _settings = XbrdSurfaceSettings.Fallback(ex.Message);
        }

        _publisher = new XbrdPublisherClient(_settings.Timeout);
        AddSessionEntry(
            XbrdTimelineEntry.LevelInfo,
            $"来源与配额 Surface 已加载（设置：{_settings.OriginText}）。");

        await RefreshAsync(userInitiated: false).ConfigureAwait(true);

        if (_settings.AutoRefresh)
        {
            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _autoRefreshTimer.Tick += OnAutoRefreshTick;
            _autoRefreshTimer.Start();
        }

        await RefreshLogsAsync().ConfigureAwait(true);
    }

    /// <summary>Starts or restarts the visible refresh. Reentrancy-safe.</summary>
    public async Task RefreshAsync(bool userInitiated)
    {
        if (_isRefreshing || _publisher is null || _disposed)
        {
            return;
        }

        _isRefreshing = true;
        IsBusy = true;
        BusyText = "正在读取发布器与 Service Unit…";
        RefreshCommand.NotifyCanExecuteChanged();
        try
        {
            var token = _lifetime.Token;
            var healthTask = _publisher.GetHealthAsync(_settings.PublisherUrl, token);
            var sourcesTask = LoadSourcesAsync(token);
            var panelTask = _publisher.GetPanelAsync(_settings.PublisherUrl, token);
            var unitsTask = LoadServiceUnitsAsync(token);
            var taskTask = _local.QueryUiQuotaTaskAsync(token);

            await Task.WhenAll(healthTask, sourcesTask, unitsTask, taskTask, panelTask).ConfigureAwait(true);

            _health = healthTask.Result;
            RaiseHealth();
            ApplySources(sourcesTask.Result);
            ApplyPanel(panelTask.Result);
            ApplyServiceUnits(unitsTask.Result);
            UiQuotaTask = taskTask.Result;
            OnPropertyChanged(nameof(UiQuotaTask));
            OnPropertyChanged(nameof(CollectorPathText));
            OnPropertyChanged(nameof(WorkerLogDirectoryText));

            LastUpdatedText = XbrdFormat.LongTime(DateTimeOffset.Now);
            SyncRowManageButtons();
            RebuildTimeline();

            if (userInitiated)
            {
                AddSessionEntry(
                    XbrdTimelineEntry.LevelAction,
                    $"手动刷新完成：聚合状态 {AggregateHeadline}（{AggregateDetail}）。");
            }
        }
        catch (OperationCanceledException)
        {
            // Surface is being unloaded.
        }
        catch (Exception ex)
        {
            AddSessionEntry(XbrdTimelineEntry.LevelError, $"刷新失败：{ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Row-level refresh: re-reads the summary (the publisher exposes no per-source GET).</summary>
    public async Task RefreshSourceAsync(XbrdSourceRowViewModel row)
    {
        if (_publisher is null || _disposed)
        {
            return;
        }

        _selectedSourceId = row.SourceId;
        AddSessionEntry(XbrdTimelineEntry.LevelAction, $"重新读取 {row.SourceId} 的来源摘要…");
        await RefreshAsync(userInitiated: false).ConfigureAwait(true);

        var refreshed = Sources.FirstOrDefault(candidate => candidate.SourceId == row.SourceId);
        if (refreshed is not null)
        {
            refreshed.IsDetailOpen = true;
            SnapshotFocusText = $"{refreshed.SourceId} · {refreshed.StatusLabel} · {refreshed.TtlText}";
        }

        AddSessionEntry(
            refreshed is null ? XbrdTimelineEntry.LevelWarn : XbrdTimelineEntry.LevelInfo,
            refreshed is null
                ? $"{row.SourceId} 在刷新后不再出现于来源清单。"
                : $"{row.SourceId} 已刷新：{refreshed.StatusLabel} · {refreshed.TtlText} · {refreshed.GeneratedText}");
    }

    /// <summary>
    /// 「停用/注销」row action. Sources this tool publishes are controlled through their Service Unit;
    /// sources owned by the router cron / UI Quota Worker are reported as not manageable here
    /// (CONTRACT.md §6 forbids this tool touching them).
    /// </summary>
    public async Task ManageSourceUnitAsync(XbrdSourceRowViewModel row)
    {
        var unitId = row.Summary.OwnerUnitId;
        if (unitId is null)
        {
            AddSessionEntry(
                XbrdTimelineEntry.LevelWarn,
                $"{row.SourceId} 由 {row.OwnerText} 发布，本工具不能停用/注销；请在该发布端处理。");
            LastActionTitle = $"停用/注销 {row.SourceId}";
            LastActionResult = $"由 {row.OwnerText} 发布：该来源不在本工具的写入契约内（CONTRACT.md §6），未执行任何改动。";
            return;
        }

        var wasRunning = FindServiceUnit(unitId)?.IsRunning ?? false;
        await SetServiceUnitStateAsync(unitId, start: !wasRunning, reason: $"来源 {row.SourceId}").ConfigureAwait(true);
    }

    public async Task RestartServiceUnitAsync(XbrdServiceUnitRowViewModel row)
    {
        if (_disposed)
        {
            return;
        }

        IsActionRunning = true;
        AddSessionEntry(XbrdTimelineEntry.LevelAction, $"正在重启 {row.UnitId}…");
        try
        {
            var snapshot = await _context.ServiceUnits.RestartAsync(row.UnitId, _lifetime.Token).ConfigureAwait(true);
            await RunActionBookkeepingAsync(
                $"重启 {row.UnitId}",
                $"{row.UnitId} → {snapshot.State}（pid {snapshot.Pid?.ToString() ?? "—"}）").ConfigureAwait(true);
            AddSessionEntry(XbrdTimelineEntry.LevelInfo, $"{row.UnitId} 已重启：{snapshot.State}，pid {snapshot.Pid?.ToString() ?? "—"}。");
        }
        catch (Exception ex)
        {
            await RunActionBookkeepingAsync($"重启 {row.UnitId}", $"失败：{ex.Message}").ConfigureAwait(true);
            AddSessionEntry(XbrdTimelineEntry.LevelError, $"{row.UnitId} 重启失败：{ex.Message}");
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    public Task ToggleServiceUnitAsync(XbrdServiceUnitRowViewModel row) =>
        SetServiceUnitStateAsync(row.UnitId, start: !row.IsRunning, reason: "服务状态区按钮");

    private async Task SetServiceUnitStateAsync(string unitId, bool start, string reason)
    {
        if (_disposed)
        {
            return;
        }

        IsActionRunning = true;
        var verb = start ? "启动" : "停止";
        AddSessionEntry(XbrdTimelineEntry.LevelAction, $"正在{verb} {unitId}（{reason}）…");
        try
        {
            var snapshot = start
                ? await _context.ServiceUnits.StartAsync(unitId, _lifetime.Token).ConfigureAwait(true)
                : await _context.ServiceUnits.StopAsync(unitId, _lifetime.Token).ConfigureAwait(true);
            await RunActionBookkeepingAsync($"{verb} {unitId}", $"{unitId} → {snapshot.State}").ConfigureAwait(true);
            AddSessionEntry(XbrdTimelineEntry.LevelInfo, $"{unitId} 已{verb}：{snapshot.State}");
        }
        catch (Exception ex)
        {
            await RunActionBookkeepingAsync($"{verb} {unitId}", $"失败：{ex.Message}").ConfigureAwait(true);
            AddSessionEntry(XbrdTimelineEntry.LevelError, $"{unitId} {verb}失败：{ex.Message}");
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    private async Task RestartAllUnitsAsync()
    {
        foreach (var unitId in CollectServiceUnitIds)
        {
            var snapshot = FindServiceUnit(unitId);
            if (snapshot is null)
            {
                continue;
            }

            await RestartServiceUnitAsync(snapshot).ConfigureAwait(true);
        }
    }

    /// <summary>「立即采集 UI 配额」: runs the local collector and reports progress + result.</summary>
    private async Task CollectUiQuotaAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsActionRunning = true;
        LastActionTitle = "立即采集 UI 配额";
        LastActionResult = "正在运行本机采集器（最长 6 分钟）…";
        AddSessionEntry(XbrdTimelineEntry.LevelAction, $"开始立即采集 UI 配额：{_local.CollectorPathText}");
        try
        {
            var result = await _local
                .CollectUiQuotaAsync(_settings, TimeSpan.FromMinutes(6), _lifetime.Token)
                .ConfigureAwait(true);
            LastActionResult = $"{result.Summary}\n{result.Tail(3000)}".Trim();
            AddSessionEntry(
                result.Succeeded ? XbrdTimelineEntry.LevelInfo : XbrdTimelineEntry.LevelError,
                $"UI 配额采集 {result.Summary}");
        }
        catch (Exception ex)
        {
            LastActionResult = $"失败：{ex.Message}";
            AddSessionEntry(XbrdTimelineEntry.LevelError, $"UI 配额采集失败：{ex.Message}");
        }
        finally
        {
            IsActionRunning = false;
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    /// <summary>「打开配额验证窗口」: starts the persistent Edge CDP session used by the collector.</summary>
    private async Task OpenQuotaWindowAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsActionRunning = true;
        LastActionTitle = "打开配额验证窗口";
        LastActionResult = "正在启动 Edge 验证会话…";
        try
        {
            var result = await _local
                .OpenQuotaVerificationWindowAsync(_settings, _lifetime.Token)
                .ConfigureAwait(true);
            LastActionResult = $"{result.Summary}\n{result.Tail(1200)}".Trim();
            AddSessionEntry(
                result.Succeeded ? XbrdTimelineEntry.LevelInfo : XbrdTimelineEntry.LevelError,
                $"配额验证窗口 {result.Summary}");
        }
        catch (Exception ex)
        {
            LastActionResult = $"失败：{ex.Message}";
            AddSessionEntry(XbrdTimelineEntry.LevelError, $"打开配额验证窗口失败：{ex.Message}");
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    private async Task RefreshLogsAsync()
    {
        if (_disposed)
        {
            return;
        }

        var lines = new List<string>();
        try
        {
            foreach (var unitId in CollectServiceUnitIds)
            {
                var count = 0;
                await foreach (var entry in _context.ServiceUnits.TailLogsAsync(unitId, _lifetime.Token).ConfigureAwait(true))
                {
                    lines.Add(new XbrdUnitLogEntry(entry.Time, unitId, entry.Level, entry.Message).Line);
                    if (++count >= 60)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"（读取 Service Unit 日志失败：{ex.Message}）");
        }

        if (lines.Count == 0)
        {
            lines.Add("（Service Unit 暂无日志；本机采集器日志目录见下方「日志」区）");
        }

        UnitLogText = string.Join("\n", lines);
        OnPropertyChanged(nameof(UnitLogText));
    }

    private async Task OpenLogsFolderAsync()
    {
        IsActionRunning = true;
        try
        {
            var result = await _local.OpenDirectoryAsync(_local.WorkerLogDirectory, _lifetime.Token).ConfigureAwait(true);
            LastActionTitle = "打开日志目录";
            LastActionResult = result.Started
                ? $"{result.Summary}：{_local.WorkerLogDirectory}"
                : $"{result.Summary}（{_local.WorkerLogDirectory}）";
            AddSessionEntry(
                result.Started ? XbrdTimelineEntry.LevelAction : XbrdTimelineEntry.LevelWarn,
                $"打开日志目录：{_local.WorkerLogDirectory}（{result.Summary}）");
        }
        catch (Exception ex)
        {
            LastActionResult = $"失败：{ex.Message}";
            AddSessionEntry(XbrdTimelineEntry.LevelError, $"打开日志目录失败：{ex.Message}");
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    // ---------------------------------------------------------------- loading helpers

    /// <summary>
    /// Direct HTTP against the publisher, then the Shell's declared <c>xbrd.sources.reload</c>
    /// command as a fallback. The command path is expected to be unavailable for a remote-http tool;
    /// whichever path produced the data is recorded in the table meta line.
    /// </summary>
    private async Task<XbrdSourcesSnapshot> LoadSourcesAsync(CancellationToken cancellationToken)
    {
        if (_publisher is null)
        {
            return XbrdSourcesSnapshot.Failed("", "客户端未初始化。", "none", DateTimeOffset.Now);
        }

        var direct = await _publisher.GetSourcesAsync(_settings.PublisherUrl, cancellationToken).ConfigureAwait(true);
        if (direct.Ok)
        {
            return direct;
        }

        try
        {
            var result = await _context
                .ExecuteCommandAsync("xbrd.sources.reload", null, cancellationToken)
                .ConfigureAwait(true);
            var body = result.Output?.Trim() ?? "";
            if (result.Success && body.StartsWith('{'))
            {
                var viaCommand = XbrdPublisherClient.ParseSources(
                    body,
                    XbrdPublisherClient.BuildEndpoint(_settings.PublisherUrl, "/api/v1/sources"),
                    "shell-command");
                if (viaCommand.Ok)
                {
                    return viaCommand;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to the direct error; the command path is documented as unavailable.
        }

        return direct;
    }

    private async Task<IReadOnlyList<(string UnitId, ServiceUnitSnapshot? Snapshot, string Error)>> LoadServiceUnitsAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<(string UnitId, ServiceUnitSnapshot? Snapshot, string Error)>();
        try
        {
            var units = await _context.ServiceUnits.ListAsync(cancellationToken).ConfigureAwait(true);
            foreach (var unitId in CollectServiceUnitIds)
            {
                var match = units.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, unitId, StringComparison.OrdinalIgnoreCase));
                result.Add((unitId, match, match is null ? "未在 ServiceManager 中注册；检查 service-units 发布结果。" : ""));
            }
        }
        catch (Exception ex)
        {
            foreach (var unitId in CollectServiceUnitIds)
            {
                result.Add((unitId, null, $"读取失败：{ex.Message}"));
            }
        }

        return result;
    }

    private void ApplySources(XbrdSourcesSnapshot snapshot)
    {
        _sources = snapshot;
        Sources.Clear();
        foreach (var source in snapshot.Sources)
        {
            var row = new XbrdSourceRowViewModel(source, this)
            {
                IsDetailOpen = source.SourceId == _selectedSourceId
            };
            Sources.Add(row);
        }

        OnPropertyChanged(nameof(SourcesPillToken));
        OnPropertyChanged(nameof(SourcesLabel));
        OnPropertyChanged(nameof(SourcesSummaryText));
        OnPropertyChanged(nameof(HasSourcesError));
        OnPropertyChanged(nameof(SourcesErrorText));
        OnPropertyChanged(nameof(IsSourcesEmpty));
        OnPropertyChanged(nameof(SourcesMetaText));
        RaiseAggregate();
    }

    private void ApplyServiceUnits(IReadOnlyList<(string UnitId, ServiceUnitSnapshot? Snapshot, string Error)> units)
    {
        ServiceUnits.Clear();
        foreach (var (unitId, snapshot, error) in units)
        {
            ServiceUnits.Add(new XbrdServiceUnitRowViewModel(
                unitId,
                DisplayNameFor(unitId),
                snapshot,
                error,
                this));
        }

        ServiceUnitsError = units.FirstOrDefault(unit => unit.Snapshot is null).Error ?? "";
        if (ServiceUnits.Count > 0 && units.All(unit => unit.Snapshot is null) && ServiceUnitsError.Length == 0)
        {
            ServiceUnitsError = "两个采集服务均未注册。";
        }

        RaiseUnits();
    }

    private void ApplyPanel(XbrdPanelSnapshot snapshot)
    {
        _panelPreview = snapshot;

        PanelStatusFields.Clear();
        foreach (var field in snapshot.StatusFields)
        {
            PanelStatusFields.Add(field);
        }

        PanelWeatherFields.Clear();
        foreach (var field in snapshot.WeatherFields)
        {
            PanelWeatherFields.Add(field);
        }

        PanelPlanFields.Clear();
        foreach (var field in snapshot.PlanFields)
        {
            PanelPlanFields.Add(field);
        }

        PanelQuotaRows.Clear();
        foreach (var row in snapshot.QuotaRows)
        {
            PanelQuotaRows.Add(row);
        }

        OnPropertyChanged(nameof(PanelPreviewPillToken));
        OnPropertyChanged(nameof(PanelPreviewLabel));
        OnPropertyChanged(nameof(PanelPreviewIsReady));
        OnPropertyChanged(nameof(PanelPreviewIsDegraded));
        OnPropertyChanged(nameof(PanelPreviewIsError));
        OnPropertyChanged(nameof(HasPanelPreviewError));
        OnPropertyChanged(nameof(PanelPreviewErrorText));
        OnPropertyChanged(nameof(PanelPreviewUpdatedText));
        OnPropertyChanged(nameof(PanelPreviewEndpoint));
        OnPropertyChanged(nameof(PanelPreviewMetaText));
        OnPropertyChanged(nameof(HasPanelStatus));
        OnPropertyChanged(nameof(HasPanelWeather));
        OnPropertyChanged(nameof(HasPanelPlan));
        OnPropertyChanged(nameof(HasPanelQuota));
        OnPropertyChanged(nameof(HasPanelPlanTodos));
        OnPropertyChanged(nameof(PanelPlanTodosText));
    }

    private void RaiseHealth()
    {        OnPropertyChanged(nameof(PanelPillToken));
        OnPropertyChanged(nameof(PanelLabel));
        OnPropertyChanged(nameof(PanelDetail));
        OnPropertyChanged(nameof(PanelEndpoint));
        OnPropertyChanged(nameof(PanelCountersText));
        RaiseAggregate();
    }

    private void RaiseUnits()
    {        OnPropertyChanged(nameof(UnitsSeverity));
        OnPropertyChanged(nameof(UnitsPillToken));
        OnPropertyChanged(nameof(UnitsLabel));
        OnPropertyChanged(nameof(UnitsSummaryText));
        OnPropertyChanged(nameof(UnitsDetail));
        OnPropertyChanged(nameof(HasServiceUnitsError));
        OnPropertyChanged(nameof(ServiceUnitsErrorText));
        RaiseAggregate();
    }

    private static string DisplayNameFor(string unitId) => unitId switch
    {
        XbrdSourceOwnership.MemServiceUnitId => "XBRD 内存来源服务",
        XbrdSourceOwnership.CodexQuotaServiceUnitId => "XBRD Codex 配额服务",
        _ => unitId
    };

    private XbrdServiceUnitRowViewModel? FindServiceUnit(string unitId) =>
        ServiceUnits.FirstOrDefault(unit => string.Equals(unit.UnitId, unitId, StringComparison.OrdinalIgnoreCase));

    private void SyncRowManageButtons()
    {
        foreach (var row in Sources)
        {
            if (!row.Summary.IsOwnedByThisTool)
            {
                row.ManageButtonText = "停用/注销";
                continue;
            }

            row.ManageButtonText = FindServiceUnit(row.Summary.OwnerUnitId!)?.IsRunning == true
                ? "停用服务"
                : "启用服务";
        }
    }

    // ---------------------------------------------------------------- timeline

    /// <summary>Failure → retry → degradation events derived from the source table, plus session actions.</summary>
    private void RebuildTimeline()
    {
        var derived = new List<XbrdTimelineEntry>();
        if (!_sources.Ok)
        {
            derived.Add(new XbrdTimelineEntry(
                _sources.FetchedAt,
                XbrdTimelineEntry.LevelError,
                $"来源清单读取失败：{_sources.Error}"));
        }
        else
        {
            foreach (var source in _sources.Sources)
            {
                var time = source.GeneratedAt ?? _sources.FetchedAt;
                if (source.HasError)
                {
                    derived.Add(new XbrdTimelineEntry(
                        time,
                        source.Severity == XbrdSeverity.Error ? XbrdTimelineEntry.LevelError : XbrdTimelineEntry.LevelWarn,
                        $"{source.SourceId} 失败：{source.Error}"));
                }
                else if (source.Expired || source.Severity == XbrdSeverity.Degraded)
                {
                    derived.Add(new XbrdTimelineEntry(
                        time,
                        XbrdTimelineEntry.LevelWarn,
                        $"{source.SourceId} 降级：{source.TtlText}（{source.AgeText} 未更新）"));
                }
            }
        }

        var merged = derived
            .Concat(_sessionEntries)
            .OrderByDescending(static entry => entry.Time)
            .Take(16)
            .ToList();

        Timeline.Clear();
        foreach (var entry in merged)
        {
            Timeline.Add(entry);
        }

        OnPropertyChanged(nameof(IsTimelineEmpty));
    }

    private void AddSessionEntry(string level, string message)
    {
        _sessionEntries.Insert(0, new XbrdTimelineEntry(DateTimeOffset.Now, level, message));
        if (_sessionEntries.Count > 40)
        {
            _sessionEntries.RemoveRange(40, _sessionEntries.Count - 40);
        }

        RebuildTimeline();
    }

    private async Task RunActionBookkeepingAsync(string title, string result)
    {
        LastActionTitle = title;
        LastActionResult = result;
        await RefreshAsync(userInitiated: false).ConfigureAwait(true);
    }

    private void RaiseAggregate()
    {
        OnPropertyChanged(nameof(AggregateSeverity));
        OnPropertyChanged(nameof(AggregateHeadline));
        OnPropertyChanged(nameof(AggregatePillToken));
        OnPropertyChanged(nameof(AggregateDetail));
        OnPropertyChanged(nameof(AggregateIsReady));
        OnPropertyChanged(nameof(AggregateIsDegraded));
        OnPropertyChanged(nameof(AggregateIsError));
        OnPropertyChanged(nameof(PanelIsReady));
        OnPropertyChanged(nameof(PanelIsDegraded));
        OnPropertyChanged(nameof(PanelIsError));
        OnPropertyChanged(nameof(SourcesIsReady));
        OnPropertyChanged(nameof(SourcesIsDegraded));
        OnPropertyChanged(nameof(SourcesIsError));
        OnPropertyChanged(nameof(UnitsIsReady));
        OnPropertyChanged(nameof(UnitsIsDegraded));
        OnPropertyChanged(nameof(UnitsIsError));
    }

    private void OnAutoRefreshTick(object? sender, EventArgs eventArgs)
    {
        if (_disposed || _isRefreshing)
        {
            return;
        }

        _ = RefreshAsync(userInitiated: false);
    }

    public void SelectSource(XbrdSourceRowViewModel? row)
    {
        _selectedSourceId = row?.SourceId ?? "";
        SnapshotFocusText = row is null
            ? ""
            : $"{row.SourceId} · {row.StatusLabel} · {row.TtlText}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_autoRefreshTimer is not null)
        {
            _autoRefreshTimer.Tick -= OnAutoRefreshTick;
            _autoRefreshTimer.Stop();
            _autoRefreshTimer = null;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _publisher?.Dispose();
        _publisher = null;
    }
}
