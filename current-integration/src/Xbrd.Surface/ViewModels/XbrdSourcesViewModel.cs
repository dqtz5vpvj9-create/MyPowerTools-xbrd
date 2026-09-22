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
/// This page is a <b>management surface</b>, not a viewer: every row action talks to the thing that
/// actually produces the source —
///  · router control plane (CONTRACT §6): <c>POST refresh|disable|enable</c>, <c>DELETE</c>,
///    <c>GET log</c>, gated by the <c>capabilities</c> the publisher declares per source;
///  · local Service Unit control endpoints (CONTRACT §5): <c>POST /publish-now</c> for
///    <c>quota.mem</c>/<c>quota.codex</c>, plus stop/start of the owning unit;
///  · local UI Quota worker actions for <c>quota.proxy</c>/<c>quota.mes</c>.
///
/// Information architecture (Lead review, task-9): the table row keeps only
/// source / status / TTL / error / owner / actions; <c>revision</c>, <c>changed_fields</c> and the raw
/// snapshot live in the 「查看快照」 drawer; <c>quota</c> is not repeated here (it belongs to the panel
/// Tab); the timeline records <b>state transitions only</b> — never clicks or "re-reading…" echoes.
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
    private readonly List<XbrdTimelineEntry> _transitions = [];
    private readonly Dictionary<string, SourceState> _previousSourceStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previousUnitStates = new(StringComparer.Ordinal);
    private XbrdPublisherClient? _publisher;
    private XbrdUnitControlClient? _unitControl;
    private XbrdSurfaceSettings _settings = XbrdSurfaceSettings.Fallback("not initialised");
    private XbrdSourcesSnapshot _sources = XbrdSourcesSnapshot.Failed("", "尚未读取。", "none", DateTimeOffset.Now);
    private XbrdHealthSnapshot _health = XbrdHealthSnapshot.Failed("", "尚未探测。");
    private XbrdPanelSnapshot _panelPreview = XbrdPanelSnapshot.Empty;
    private XbrdUiQuotaTaskStatus _uiQuotaTask = XbrdUiQuotaTaskStatus.Failed("尚未查询。", []);
    private DispatcherTimer? _autoRefreshTimer;
    private bool _isRefreshing;
    private bool _isBusy;
    private bool _isActionRunning;
    private bool _isRefreshAllRunning;
    private bool _hadSourcesFetchFailure;
    private bool _controlPlaneRejected;
    private string _busyText = "正在读取发布器与 Service Unit…";
    private string _refreshAllProgressText = "";
    private string _lastActionTitle = "";
    private string _lastActionResult = "";
    private string _unitLogText = "（尚未读取日志）";
    private string _lastUpdatedText = "—";
    private string _serviceUnitsError = "";
    private bool _disposed;

    // ---- drawer state ----
    private bool _isDrawerOpen;
    private string _drawerSourceId = "";
    private string _drawerTitle = "";
    private string _drawerLogText = "";
    private string _drawerLogMetaText = "";
    private bool _isDrawerLogLoading;

    public XbrdSourcesViewModel(MptAvaloniaSurfaceContext context)
    {
        _context = context;

        RefreshCommand = new MptAsyncRelayCommand(
            () => RefreshAsync(userInitiated: true),
            () => !_isRefreshing,
            "xbrd.sources.refresh");
        RefreshAllCommand = new MptAsyncRelayCommand(
            RefreshAllSourcesAsync,
            () => !_isRefreshAllRunning,
            "xbrd.sources.refresh-all");
        CloseDrawerCommand = new MptAsyncRelayCommand(
            () =>
            {
                IsDrawerOpen = false;
                return Task.CompletedTask;
            },
            operationName: "xbrd.drawer.close");
        LoadDrawerLogCommand = new MptAsyncRelayCommand(
            LoadDrawerLogAsync,
            () => !_isDrawerLogLoading,
            "xbrd.drawer.log");
        RefreshLogsCommand = new MptAsyncRelayCommand(RefreshLogsAsync, () => !_isActionRunning, "xbrd.logs.refresh");
        OpenLogsFolderCommand = new MptAsyncRelayCommand(OpenLogsFolderAsync, () => !_isActionRunning, "xbrd.logs.folder");
    }

    private readonly record struct SourceState(XbrdSeverity Severity, bool Enabled, string Effective);

    // ---------------------------------------------------------------- headline / aggregate

    public string AggregateHeadline => AggregateSeverity.ToLabel();

    public string AggregatePillToken => AggregateSeverity.ToToken();

    public XbrdSeverity AggregateSeverity => _health.Severity.Worst(_sources.Severity).Worst(UnitsSeverity);

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

    /// <summary>Explicit control-plane state: old publisher / rejected endpoints must never be silent.</summary>
    public string ControlPlaneText => _sources.ControlPlaneText;

    public bool HasControlPlaneWarning => !_sources.Ok || !_sources.ControlPlaneAvailable || _controlPlaneRejected;

    public string ControlPlaneWarningText => !_sources.Ok
        ? $"路由器不可达或来源清单读取失败：{_sources.Error}"
        : _controlPlaneRejected
            ? "路由器控制面返回 404/405：当前发布器不支持 refresh/disable/delete/log，行内只保留本地动作。需要 D2 的控制面部署。"
            : !_sources.ControlPlaneAvailable
                ? "路由器未返回 capabilities 字段：这是旧发布器，行内只保留本地动作（立即发布 / 采集 / 验证窗口）。"
                : "";

    // ---------------------------------------------------------------- panel preview

    public ObservableCollection<XbrdPanelField> PanelStatusFields { get; } = [];

    public ObservableCollection<XbrdPanelField> PanelWeatherFields { get; } = [];

    public ObservableCollection<XbrdPanelField> PanelPlanFields { get; } = [];

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

    public bool HasPanelPlanTodos => _panelPreview.TodosText.Length > 0;

    public string PanelPlanTodosText => _panelPreview.TodosText;

    // ---------------------------------------------------------------- 全部刷新

    public bool IsRefreshAllRunning
    {
        get => _isRefreshAllRunning;
        private set
        {
            if (SetProperty(ref _isRefreshAllRunning, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(WorkingText));
                RefreshAllCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RefreshAllProgressText
    {
        get => _refreshAllProgressText;
        private set
        {
            if (SetProperty(ref _refreshAllProgressText, value))
            {
                OnPropertyChanged(nameof(HasRefreshAllProgress));
            }
        }
    }

    public bool HasRefreshAllProgress => _refreshAllProgressText.Length > 0;

    public ObservableCollection<XbrdRefreshAllResult> RefreshAllResults { get; } = [];

    public bool HasRefreshAllResults => RefreshAllResults.Count > 0;

    /// <summary>Number of sources whose publisher declared <c>refresh</c>; drives the button hint.</summary>
    public int RefreshableCount => _sources.RefreshableCount;

    public string RefreshAllHint => _sources.Ok
        ? _sources.ControlPlaneAvailable
            ? $"对声明了 refresh 能力的 {RefreshableCount} 个来源逐个执行（限并发 2）"
            : "路由器未提供控制面，无法执行远端刷新"
        : "路由器不可达";

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

    public string ServiceUnitsError
    {
        get => _serviceUnitsError;
        private set
        {
            if (SetProperty(ref _serviceUnitsError, value))
            {
                OnPropertyChanged(nameof(HasServiceUnitsError));
                OnPropertyChanged(nameof(ServiceUnitsErrorText));
                RaiseUnits();
            }
        }
    }

    public XbrdUiQuotaTaskStatus UiQuotaTask => _uiQuotaTask;

    public string UiQuotaTaskName => XbrdUiQuotaTaskStatus.TaskPathAndName;

    public string CollectorPathText => _local.CollectorPathText;

    public string WorkerLogDirectoryText => _local.WorkerLogDirectory;

    // ---------------------------------------------------------------- local actions / logs

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

    /// <summary>True while any asynchronous work (refresh, refresh-all or a local action) is in flight.</summary>
    public bool IsWorking => _isBusy || _isActionRunning || _isRefreshAllRunning;

    public string WorkingText => _isRefreshAllRunning
        ? _refreshAllProgressText
        : _isActionRunning
            ? "正在执行本机动作…"
            : _busyText;

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

    // ---------------------------------------------------------------- drawer

    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        private set => SetProperty(ref _isDrawerOpen, value);
    }

    public string DrawerTitle
    {
        get => _drawerTitle;
        private set => SetProperty(ref _drawerTitle, value);
    }

    public string DrawerSourceId
    {
        get => _drawerSourceId;
        private set => SetProperty(ref _drawerSourceId, value);
    }

    public string DrawerLogText
    {
        get => _drawerLogText;
        private set
        {
            if (SetProperty(ref _drawerLogText, value))
            {
                OnPropertyChanged(nameof(HasDrawerLog));
            }
        }
    }

    public bool HasDrawerLog => _drawerLogText.Length > 0;

    public string DrawerLogMetaText
    {
        get => _drawerLogMetaText;
        private set => SetProperty(ref _drawerLogMetaText, value);
    }

    public bool IsDrawerLogLoading
    {
        get => _isDrawerLogLoading;
        private set
        {
            if (SetProperty(ref _isDrawerLogLoading, value))
            {
                LoadDrawerLogCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private XbrdSourceRowViewModel? DrawerSource =>
        Sources.FirstOrDefault(row => string.Equals(row.SourceId, _drawerSourceId, StringComparison.Ordinal));

    public string DrawerStatusText => DrawerSource is { } row
        ? $"{row.StatusLabel} · {row.TtlText} · {row.OwnerText}"
        : "—";

    public string DrawerEnabledText => DrawerSource is { } row
        ? $"{row.EnabledText} · 能力：{row.CapabilitiesText}"
        : "—";

    public string DrawerRevisionText => DrawerSource?.RevisionText ?? "—";

    public string DrawerGeneratedText => DrawerSource is { } row
        ? $"generated {row.GeneratedText} · received {row.ReceivedText}"
        : "—";

    public string DrawerChangedFieldsText => DrawerSource?.ChangedFieldsText ?? "—";

    public string DrawerErrorText => DrawerSource?.ErrorText ?? "—";

    public string DrawerHintText => DrawerSource?.ManageHint ?? "";

    public bool DrawerCanLoadLog => DrawerSource?.ShowLogAction ?? false;

    /// <summary>Raw snapshot JSON as the publisher reported it (source_id + every parsed field).</summary>
    public string DrawerSnapshotJson
    {
        get
        {
            if (DrawerSource is not { } row)
            {
                return "";
            }

            var summary = row.Summary;
            var payload = new System.Text.Json.Nodes.JsonObject
            {
                ["schema"] = "xbrd.source.summary.v1",
                ["source_id"] = summary.SourceId,
                ["kind"] = summary.Kind,
                ["status"] = summary.Status,
                ["effective_status"] = summary.EffectiveStatus,
                ["enabled"] = summary.Enabled,
                ["expired"] = summary.Expired,
                ["age_s"] = summary.AgeSeconds,
                ["ttl_s"] = summary.TtlSeconds,
                ["generated_at"] = summary.GeneratedAt?.ToString("O") ?? "",
                ["received_at"] = summary.ReceivedAt?.ToString("O") ?? "",
                ["revision"] = summary.Revision,
                ["changed_fields"] = new System.Text.Json.Nodes.JsonArray(
                    summary.ChangedFields.Select(static name => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(name)).ToArray()),
                ["error"] = summary.Error,
                ["capabilities"] = new System.Text.Json.Nodes.JsonArray(
                    (summary.Capabilities ?? []).Select(static name => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(name)).ToArray())
            };
            return payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
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

    // ---------------------------------------------------------------- timeline (transitions only)

    public ObservableCollection<XbrdTimelineEntry> Timeline { get; } = [];

    public bool IsTimelineEmpty => Timeline.Count == 0;

    // ---------------------------------------------------------------- commands

    public MptAsyncRelayCommand RefreshCommand { get; }

    public MptAsyncRelayCommand RefreshAllCommand { get; }

    public MptAsyncRelayCommand CloseDrawerCommand { get; }

    public MptAsyncRelayCommand LoadDrawerLogCommand { get; }

    public ICommand RefreshLogsCommand { get; }

    public ICommand OpenLogsFolderCommand { get; }

    // ---------------------------------------------------------------- lifecycle

    public async Task InitializeAsync()
    {
        try
        {
            _settings = await XbrdSettingsLoader.LoadAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _settings = XbrdSurfaceSettings.Fallback(ex.Message);
        }

        _publisher = new XbrdPublisherClient(_settings.Timeout);
        _unitControl = new XbrdUnitControlClient(TimeSpan.FromSeconds(20));

        await RefreshAsync(userInitiated: false).ConfigureAwait(true);

        if (_settings.AutoRefresh)
        {
            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _autoRefreshTimer.Tick += OnAutoRefreshTick;
            _autoRefreshTimer.Start();
        }

        await RefreshLogsAsync().ConfigureAwait(true);
    }

    /// <summary>Starts or restarts the visible refresh. Reentrancy-safe; never records a timeline echo.</summary>
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
            _uiQuotaTask = taskTask.Result;
            OnPropertyChanged(nameof(UiQuotaTask));
            OnPropertyChanged(nameof(CollectorPathText));
            OnPropertyChanged(nameof(WorkerLogDirectoryText));

            LastUpdatedText = XbrdFormat.LongTime(DateTimeOffset.Now);
            SyncRowState();
            RebuildTimeline();
            RaiseDrawer();
        }
        catch (OperationCanceledException)
        {
            // Surface is being unloaded.
        }
        catch (Exception ex)
        {
            AddTransition(XbrdTimelineEntry.LevelError, $"刷新失败：{ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    // ---------------------------------------------------------------- router control actions

    /// <summary>
    /// Real refresh: <c>POST /api/v1/sources/&lt;id&gt;/refresh</c> makes the publisher run that source's
    /// collection plugin. The row shows the publisher's exit code, duration and stdout tail.
    /// </summary>
    public async Task RefreshSourceAsync(XbrdSourceRowViewModel row)
    {
        if (_publisher is null || _disposed || row.IsBusy)
        {
            return;
        }

        if (!row.Summary.CanRefresh)
        {
            row.SetOutcome(XbrdControlResult.Declined("refresh", row.SourceId, "路由器未对该来源声明 refresh 能力。"));
            return;
        }

        row.IsBusy = true;
        row.BusyText = "正在执行远端采集…";
        try
        {
            var result = await _publisher
                .RefreshSourceAsync(_settings.PublisherUrl, row.SourceId, _lifetime.Token)
                .ConfigureAwait(true);
            ApplyControlResult(row, result);
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = "";
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    public async Task ToggleSourceEnabledAsync(XbrdSourceRowViewModel row)
    {
        if (_publisher is null || _disposed || row.IsBusy || !row.Summary.CanDisable)
        {
            return;
        }

        var enable = !row.Enabled;
        row.IsBusy = true;
        row.BusyText = enable ? "正在启用…" : "正在停用…";
        try
        {
            var result = await _publisher
                .SetSourceEnabledAsync(_settings.PublisherUrl, row.SourceId, enable, _lifetime.Token)
                .ConfigureAwait(true);
            ApplyControlResult(row, result);
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = "";
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    /// <summary>Delete is only offered when the publisher declares it; the row arms a confirm step first.</summary>
    public async Task DeleteSourceAsync(XbrdSourceRowViewModel row)
    {
        if (_publisher is null || _disposed || row.IsBusy)
        {
            return;
        }

        row.ClearDeleteConfirm();
        if (!row.Summary.CanDelete)
        {
            row.SetOutcome(XbrdControlResult.Declined("delete", row.SourceId, "路由器未对该来源声明 delete 能力（有生产者的来源只能停用）。"));
            return;
        }

        row.IsBusy = true;
        row.BusyText = "正在注销…";
        try
        {
            var result = await _publisher
                .DeleteSourceAsync(_settings.PublisherUrl, row.SourceId, _lifetime.Token)
                .ConfigureAwait(true);
            ApplyControlResult(row, result);
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = "";
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    /// <summary>Loads real log lines for one source into the row and the drawer.</summary>
    public async Task LoadSourceLogAsync(XbrdSourceRowViewModel row)
    {
        if (_publisher is null || _disposed)
        {
            return;
        }

        OpenDrawer(row);
        await LoadLogCoreAsync(row).ConfigureAwait(true);
    }

    private async Task LoadLogCoreAsync(XbrdSourceRowViewModel row)
    {
        if (_publisher is null)
        {
            return;
        }

        IsDrawerLogLoading = true;
        DrawerLogMetaText = "正在读取日志…";
        try
        {
            var result = await _publisher
                .GetSourceLogAsync(_settings.PublisherUrl, row.SourceId, 200, _lifetime.Token)
                .ConfigureAwait(true);
            DrawerLogText = result.Text;
            DrawerLogMetaText = result.MetaText;
            row.SetLog(result.Text);
            if (!result.Ok)
            {
                row.SetOutcome($"日志读取失败\n{result.Error}", true);
            }
        }
        finally
        {
            IsDrawerLogLoading = false;
        }
    }

    private void ApplyControlResult(XbrdSourceRowViewModel row, XbrdControlResult result)
    {
        row.SetOutcome(result);
        if (result.Status == XbrdControlStatus.Unsupported && !_controlPlaneRejected)
        {
            _controlPlaneRejected = true;
            RaiseControlPlane();
        }
        else if (result.Ok && _controlPlaneRejected)
        {
            // The control plane answered after all (e.g. D deployed it while we were open).
            _controlPlaneRejected = false;
            RaiseControlPlane();
        }
    }

    // ---------------------------------------------------------------- 全部刷新

    /// <summary>
    /// Runs the real refresh for every source that declares <c>refresh</c>, at most two at a time,
    /// with per-item progress and results.
    /// </summary>
    public async Task RefreshAllSourcesAsync()
    {
        if (_publisher is null || _disposed || _isRefreshAllRunning)
        {
            return;
        }

        var targets = Sources.Where(static row => row.Summary.CanRefresh).ToArray();
        RefreshAllResults.Clear();
        OnPropertyChanged(nameof(HasRefreshAllResults));
        if (targets.Length == 0)
        {
            RefreshAllProgressText = _sources.ControlPlaneAvailable
                ? "没有来源声明 refresh 能力，无法执行远端刷新。"
                : "路由器未提供控制面（旧发布器），无法执行远端刷新。";
            return;
        }

        IsRefreshAllRunning = true;
        var completed = 0;
        var succeeded = 0;
        var failed = 0;
        RefreshAllProgressText = $"全部刷新 0/{targets.Length}…";
        try
        {
            using var gate = new SemaphoreSlim(2);
            var tasks = targets.Select(async row =>
            {
                await gate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
                try
                {
                    if (!row.Summary.CanRefresh)
                    {
                        return;
                    }

                    row.IsBusy = true;
                    row.BusyText = "全部刷新中…";
                    XbrdControlResult result;
                    try
                    {
                        result = await _publisher
                            .RefreshSourceAsync(_settings.PublisherUrl, row.SourceId, _lifetime.Token)
                            .ConfigureAwait(true);
                    }
                    finally
                    {
                        row.IsBusy = false;
                        row.BusyText = "";
                    }

                    ApplyControlResult(row, result);
                    lock (RefreshAllResults)
                    {
                        completed++;
                        if (result.Status == XbrdControlStatus.Ok)
                        {
                            succeeded++;
                        }
                        else
                        {
                            failed++;
                        }

                        RefreshAllResults.Insert(0, new XbrdRefreshAllResult(
                            row.SourceId,
                            result.Status == XbrdControlStatus.Ok,
                            $"{row.SourceId}：{result.HeadlineText} · {result.EvidenceText.Replace('\n', ' ')}"));
                    }

                    RefreshAllProgressText = $"全部刷新 {completed}/{targets.Length}（成功 {succeeded} · 失败 {failed}）";
                    OnPropertyChanged(nameof(HasRefreshAllResults));
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(true);
            RefreshAllProgressText = $"全部刷新完成：成功 {succeeded} · 失败 {failed}（共 {targets.Length}）";
        }
        catch (OperationCanceledException)
        {
            RefreshAllProgressText = $"全部刷新已取消（完成 {completed}/{targets.Length}）";
        }
        finally
        {
            IsRefreshAllRunning = false;
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    // ---------------------------------------------------------------- local (own) sources

    /// <summary>「立即发布」 for quota.mem / quota.codex: POST /publish-now on the owning Service Unit.</summary>
    public async Task PublishNowAsync(XbrdSourceRowViewModel row)
    {
        if (_unitControl is null || _disposed || row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        row.BusyText = "正在调用 unit /publish-now…";
        try
        {
            var result = await _unitControl.PublishNowAsync(row.SourceId, _lifetime.Token).ConfigureAwait(true);
            row.SetOutcome(result);
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = "";
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    /// <summary>停止/启动 the Service Unit that produces this source (that is what 「停用」 means locally).</summary>
    public async Task ToggleSourceUnitAsync(XbrdSourceRowViewModel row)
    {
        var unitId = row.Summary.OwnerUnitId;
        if (unitId is null || _disposed || row.IsBusy)
        {
            return;
        }

        var start = !IsUnitRunning(unitId);
        row.IsBusy = true;
        row.BusyText = start ? "正在启动服务…" : "正在停止服务…";
        try
        {
            var result = await SetServiceUnitStateCoreAsync(unitId, start, _lifetime.Token).ConfigureAwait(true);
            row.SetOutcome(result ? $"{unitId} 已{(start ? "启动" : "停止")}" : $"{unitId} {(start ? "启动" : "停止")}失败", !result);
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = "";
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    /// <summary>「立即采集 UI 配额」 for quota.proxy / quota.mes — a real local collector run.</summary>
    public async Task CollectUiQuotaAsync(XbrdSourceRowViewModel row)
    {
        if (_disposed || row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        row.BusyText = "正在运行本机采集器（最长 6 分钟）…";
        LastActionTitle = "立即采集 UI 配额";
        LastActionResult = row.BusyText;
        try
        {
            var result = await _local
                .CollectUiQuotaAsync(_settings, TimeSpan.FromMinutes(6), _lifetime.Token)
                .ConfigureAwait(true);
            LastActionResult = $"{result.Summary}\n{result.Tail(3000)}".Trim();
            row.SetOutcome($"UI 配额采集：{result.Summary}\n{result.Tail(600)}", !result.Succeeded);
        }
        catch (Exception ex)
        {
            LastActionResult = $"失败：{ex.Message}";
            row.SetOutcome($"UI 配额采集失败：{ex.Message}", true);
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = "";
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    /// <summary>「打开配额验证窗口」 for quota.proxy / quota.mes — starts the persistent Edge session.</summary>
    public async Task OpenQuotaWindowAsync(XbrdSourceRowViewModel row)
    {
        if (_disposed || row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        row.BusyText = "正在启动 Edge 验证会话…";
        LastActionTitle = "打开配额验证窗口";
        LastActionResult = row.BusyText;
        try
        {
            var result = await _local
                .OpenQuotaVerificationWindowAsync(_settings, _lifetime.Token)
                .ConfigureAwait(true);
            LastActionResult = $"{result.Summary}\n{result.Tail(1200)}".Trim();
            row.SetOutcome($"配额验证窗口：{result.Summary}\n{result.Tail(400)}", !result.Succeeded);
        }
        catch (Exception ex)
        {
            LastActionResult = $"失败：{ex.Message}";
            row.SetOutcome($"打开配额验证窗口失败：{ex.Message}", true);
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = "";
        }
    }

    public async Task ToggleServiceUnitAsync(XbrdServiceUnitRowViewModel unitRow)
    {
        if (_disposed)
        {
            return;
        }

        IsActionRunning = true;
        try
        {
            var start = !unitRow.IsRunning;
            var ok = await SetServiceUnitStateCoreAsync(unitRow.UnitId, start, _lifetime.Token).ConfigureAwait(true);
            LastActionTitle = $"{(start ? "启动" : "停止")} {unitRow.UnitId}";
            LastActionResult = ok ? "完成" : "失败（详见时间线）";
        }
        finally
        {
            IsActionRunning = false;
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    public async Task RestartServiceUnitAsync(XbrdServiceUnitRowViewModel unitRow)
    {
        if (_disposed)
        {
            return;
        }

        IsActionRunning = true;
        LastActionTitle = $"重启 {unitRow.UnitId}";
        LastActionResult = "执行中…";
        try
        {
            var snapshot = await _context.ServiceUnits.RestartAsync(unitRow.UnitId, _lifetime.Token).ConfigureAwait(true);
            LastActionResult = $"{unitRow.UnitId} → {snapshot.State}（pid {snapshot.Pid?.ToString() ?? "—"}）";
        }
        catch (Exception ex)
        {
            LastActionResult = $"失败：{ex.Message}";
        }
        finally
        {
            IsActionRunning = false;
            await RefreshAsync(userInitiated: false).ConfigureAwait(true);
        }
    }

    private async Task<bool> SetServiceUnitStateCoreAsync(string unitId, bool start, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = start
                ? await _context.ServiceUnits.StartAsync(unitId, cancellationToken).ConfigureAwait(true)
                : await _context.ServiceUnits.StopAsync(unitId, cancellationToken).ConfigureAwait(true);
            LastActionTitle = $"{(start ? "启动" : "停止")} {unitId}";
            LastActionResult = $"{unitId} → {snapshot.State}";
            return true;
        }
        catch (Exception ex)
        {
            LastActionTitle = $"{(start ? "启动" : "停止")} {unitId}";
            LastActionResult = $"失败：{ex.Message}";
            return false;
        }
    }

    public bool IsUnitRunning(string? unitId) =>
        unitId is not null && ServiceUnits.FirstOrDefault(unit =>
            string.Equals(unit.UnitId, unitId, StringComparison.OrdinalIgnoreCase))?.IsRunning == true;

    // ---------------------------------------------------------------- logs / folders

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
            lines.Add("（Service Unit 暂无日志；本机采集器日志目录见下方）");
        }

        UnitLogText = string.Join("\n", lines);
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
        }
        catch (Exception ex)
        {
            LastActionResult = $"失败：{ex.Message}";
        }
        finally
        {
            IsActionRunning = false;
        }
    }

    private Task LoadDrawerLogAsync()
    {
        var row = DrawerSource;
        return row is null ? Task.CompletedTask : LoadLogCoreAsync(row);
    }

    // ---------------------------------------------------------------- drawer

    public void OpenDrawer(XbrdSourceRowViewModel row)
    {
        DrawerSourceId = row.SourceId;
        DrawerTitle = $"{row.SourceId} · 快照详情";
        DrawerLogMetaText = row.HasLastLog ? "已载入日志（可重新读取）" : "尚未读取日志";
        DrawerLogText = row.LastLogText;
        IsDrawerOpen = true;
        RaiseDrawer();
    }

    private void RaiseDrawer()
    {
        OnPropertyChanged(nameof(DrawerStatusText));
        OnPropertyChanged(nameof(DrawerEnabledText));
        OnPropertyChanged(nameof(DrawerRevisionText));
        OnPropertyChanged(nameof(DrawerGeneratedText));
        OnPropertyChanged(nameof(DrawerChangedFieldsText));
        OnPropertyChanged(nameof(DrawerErrorText));
        OnPropertyChanged(nameof(DrawerHintText));
        OnPropertyChanged(nameof(DrawerSnapshotJson));
        OnPropertyChanged(nameof(DrawerCanLoadLog));
    }

    // ---------------------------------------------------------------- loading helpers

    /// <summary>
    /// Direct HTTP against the publisher, then the Shell's declared <c>xbrd.sources.reload</c> command
    /// as a fallback (documented as unavailable for a remote-http tool).
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

        // Rebuilding the rows must not lose the evidence a control action just produced.
        var transient = Sources.ToDictionary(
            static row => row.SourceId,
            static row => (row.OutcomeText, row.OutcomeIsError, row.LastLogText, row.IsDeleteConfirming),
            StringComparer.Ordinal);

        Sources.Clear();
        foreach (var source in snapshot.Sources)
        {
            var row = new XbrdSourceRowViewModel(source, this);
            if (transient.TryGetValue(source.SourceId, out var previous))
            {
                row.AdoptTransientState(previous.OutcomeText, previous.OutcomeIsError, previous.LastLogText, previous.IsDeleteConfirming);
            }

            Sources.Add(row);
        }

        RecordSourceTransitions(snapshot);

        OnPropertyChanged(nameof(SourcesPillToken));
        OnPropertyChanged(nameof(SourcesLabel));
        OnPropertyChanged(nameof(SourcesSummaryText));
        OnPropertyChanged(nameof(HasSourcesError));
        OnPropertyChanged(nameof(SourcesErrorText));
        OnPropertyChanged(nameof(IsSourcesEmpty));
        OnPropertyChanged(nameof(SourcesMetaText));
        OnPropertyChanged(nameof(RefreshableCount));
        OnPropertyChanged(nameof(RefreshAllHint));
        RaiseControlPlane();
        RaiseAggregate();
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
        OnPropertyChanged(nameof(HasPanelPlanTodos));
        OnPropertyChanged(nameof(PanelPlanTodosText));
    }

    private void ApplyServiceUnits(IReadOnlyList<(string UnitId, ServiceUnitSnapshot? Snapshot, string Error)> units)
    {
        ServiceUnits.Clear();
        foreach (var (unitId, snapshot, error) in units)
        {
            ServiceUnits.Add(new XbrdServiceUnitRowViewModel(unitId, DisplayNameFor(unitId), snapshot, error, this));
        }

        RecordUnitTransitions(units);
        ServiceUnitsError = units.FirstOrDefault(unit => unit.Snapshot is null).Error ?? "";
        if (ServiceUnits.Count > 0 && units.All(unit => unit.Snapshot is null) && ServiceUnitsError.Length == 0)
        {
            ServiceUnitsError = "两个采集服务均未注册。";
        }

        RaiseUnits();
    }

    private static string DisplayNameFor(string unitId) => unitId switch
    {
        XbrdSourceOwnership.MemServiceUnitId => "XBRD 内存来源服务",
        XbrdSourceOwnership.CodexQuotaServiceUnitId => "XBRD Codex 配额服务",
        _ => unitId
    };

    /// <summary>Re-evaluate every unit-dependent row label after Service Unit state changed.</summary>
    private void SyncRowState()
    {
        foreach (var row in Sources)
        {
            row.NotifyUnitStateChanged();
        }
    }

    // ---------------------------------------------------------------- timeline: transitions only

    private void RecordSourceTransitions(XbrdSourcesSnapshot snapshot)
    {
        if (!snapshot.Ok)
        {
            if (!_hadSourcesFetchFailure)
            {
                _hadSourcesFetchFailure = true;
                AddTransition(XbrdTimelineEntry.LevelError, $"来源清单读取失败：{snapshot.Error}");
            }

            return;
        }

        if (_hadSourcesFetchFailure)
        {
            _hadSourcesFetchFailure = false;
            AddTransition(XbrdTimelineEntry.LevelInfo, "来源清单恢复可读");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in snapshot.Sources)
        {
            seen.Add(source.SourceId);
            var state = new SourceState(source.Severity, source.Enabled, source.EffectiveStatus);
            if (_previousSourceStates.TryGetValue(source.SourceId, out var previous))
            {
                if (previous.Enabled != state.Enabled)
                {
                    AddTransition(
                        state.Enabled ? XbrdTimelineEntry.LevelInfo : XbrdTimelineEntry.LevelWarn,
                        $"{source.SourceId} {(state.Enabled ? "已启用" : "已停用")}");
                }
                else if (previous.Effective != state.Effective || previous.Severity != state.Severity)
                {
                    AddTransition(
                        SeverityToLevel(state.Severity),
                        $"{source.SourceId} 状态 {previous.Effective} → {state.Effective}");
                }
            }

            _previousSourceStates[source.SourceId] = state;
        }

        foreach (var removed in _previousSourceStates.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _previousSourceStates.Remove(removed);
            AddTransition(XbrdTimelineEntry.LevelWarn, $"{removed} 已从来源清单移除（注销或发布器清理）");
        }
    }

    private void RecordUnitTransitions(IReadOnlyList<(string UnitId, ServiceUnitSnapshot? Snapshot, string Error)> units)
    {
        foreach (var (unitId, snapshot, _) in units)
        {
            var state = snapshot is null ? "未注册" : snapshot.State.ToString().ToLowerInvariant();
            if (_previousUnitStates.TryGetValue(unitId, out var previous))
            {
                if (!string.Equals(previous, state, StringComparison.Ordinal))
                {
                    var level = state is "failed" or "未注册"
                        ? XbrdTimelineEntry.LevelError
                        : state == "active"
                            ? XbrdTimelineEntry.LevelInfo
                            : XbrdTimelineEntry.LevelWarn;
                    AddTransition(level, $"{unitId} {previous} → {state}");
                }
            }

            _previousUnitStates[unitId] = state;
        }
    }

    private static string SeverityToLevel(XbrdSeverity severity) => severity switch
    {
        XbrdSeverity.Error => XbrdTimelineEntry.LevelError,
        XbrdSeverity.Degraded => XbrdTimelineEntry.LevelWarn,
        _ => XbrdTimelineEntry.LevelInfo
    };

    private void AddTransition(string level, string message)
    {
        _transitions.Insert(0, new XbrdTimelineEntry(DateTimeOffset.Now, level, message));
        if (_transitions.Count > 40)
        {
            _transitions.RemoveRange(40, _transitions.Count - 40);
        }

        RebuildTimeline();
    }

    private void RebuildTimeline()
    {
        Timeline.Clear();
        foreach (var entry in _transitions.Take(16))
        {
            Timeline.Add(entry);
        }

        OnPropertyChanged(nameof(IsTimelineEmpty));
    }

    // ---------------------------------------------------------------- notifications

    private void RaiseHealth()
    {
        OnPropertyChanged(nameof(PanelPillToken));
        OnPropertyChanged(nameof(PanelLabel));
        OnPropertyChanged(nameof(PanelDetail));
        OnPropertyChanged(nameof(PanelEndpoint));
        OnPropertyChanged(nameof(PanelCountersText));
        RaiseAggregate();
    }

    private void RaiseUnits()
    {
        OnPropertyChanged(nameof(UnitsSeverity));
        OnPropertyChanged(nameof(UnitsPillToken));
        OnPropertyChanged(nameof(UnitsLabel));
        OnPropertyChanged(nameof(UnitsSummaryText));
        OnPropertyChanged(nameof(UnitsDetail));
        OnPropertyChanged(nameof(HasServiceUnitsError));
        OnPropertyChanged(nameof(ServiceUnitsErrorText));
        RaiseAggregate();
    }

    private void RaiseControlPlane()
    {
        OnPropertyChanged(nameof(ControlPlaneText));
        OnPropertyChanged(nameof(HasControlPlaneWarning));
        OnPropertyChanged(nameof(ControlPlaneWarningText));
        OnPropertyChanged(nameof(RefreshAllHint));
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
        if (_disposed || _isRefreshing || _isRefreshAllRunning || _isActionRunning)
        {
            return;
        }

        _ = RefreshAsync(userInitiated: false);
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
        _unitControl?.Dispose();
        _unitControl = null;
    }
}
