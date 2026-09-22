using System.Windows.Input;
using MyPowerTools.AvaloniaSdk;
using Xbrd.Surface.Services;

namespace Xbrd.Surface.ViewModels;

/// <summary>
/// One source row in the management table. Every action here is a real control action gated by the
/// publisher's declared <c>capabilities</c> (router) or by local ownership (Service Unit / UI Quota
/// worker); nothing on this row is a re-read dressed up as an action.
/// </summary>
public sealed class XbrdSourceRowViewModel : MptObservableViewModel
{
    private readonly XbrdSourcesViewModel _owner;
    private bool _isBusy;
    private string _busyText = "";
    private bool _isDeleteConfirming;
    private string _outcomeText = "";
    private bool _outcomeIsError;
    private string _lastLogText = "";

    public XbrdSourceRowViewModel(XbrdSourceSummary summary, XbrdSourcesViewModel owner)
    {
        Summary = summary;
        _owner = owner;

        RefreshCommand = Command(() => _owner.RefreshSourceAsync(this), $"xbrd.source.refresh:{summary.SourceId}");
        ToggleEnabledCommand = Command(() => _owner.ToggleSourceEnabledAsync(this), $"xbrd.source.toggle:{summary.SourceId}");
        ArmDeleteCommand = Command(
            () =>
            {
                IsDeleteConfirming = true;
                return Task.CompletedTask;
            },
            $"xbrd.source.delete.arm:{summary.SourceId}");
        CancelDeleteCommand = Command(
            () =>
            {
                IsDeleteConfirming = false;
                return Task.CompletedTask;
            },
            $"xbrd.source.delete.cancel:{summary.SourceId}");
        ConfirmDeleteCommand = Command(() => _owner.DeleteSourceAsync(this), $"xbrd.source.delete:{summary.SourceId}");
        ShowSnapshotCommand = Command(
            () =>
            {
                _owner.OpenDrawer(this);
                return Task.CompletedTask;
            },
            $"xbrd.source.snapshot:{summary.SourceId}");
        LoadLogCommand = Command(() => _owner.LoadSourceLogAsync(this), $"xbrd.source.log:{summary.SourceId}");
        PublishNowCommand = Command(() => _owner.PublishNowAsync(this), $"xbrd.source.publish:{summary.SourceId}");
        ToggleUnitCommand = Command(() => _owner.ToggleSourceUnitAsync(this), $"xbrd.source.unit:{summary.SourceId}");
        CollectUiQuotaCommand = Command(() => _owner.CollectUiQuotaAsync(this), $"xbrd.source.collect:{summary.SourceId}");
        OpenQuotaWindowCommand = Command(() => _owner.OpenQuotaWindowAsync(this), $"xbrd.source.edge:{summary.SourceId}");
    }

    private static MptAsyncRelayCommand Command(Func<Task> action, string operationName) =>
        new(action, operationName: operationName);

    public XbrdSourceSummary Summary { get; }

    public string SourceId => Summary.SourceId;

    public string KindText => Summary.KindText;

    public string PillToken => Summary.PillToken;

    public bool IsReady => Summary.Severity == XbrdSeverity.Ready;

    public bool IsDegraded => Summary.Severity == XbrdSeverity.Degraded;

    public bool IsError => Summary.Severity == XbrdSeverity.Error;

    public string StatusLabel => Summary.StatusLabel;

    public string TtlText => Summary.TtlText;

    public string TtlDetailText => Summary.TtlDetailText;

    public string ErrorText => Summary.ErrorText;

    public bool HasError => Summary.HasError;

    public string OwnerText => Summary.OwnerText;

    public string ManageHint => Summary.ManageHint;

    public bool Enabled => Summary.Enabled;

    public string EnabledText => Summary.EnabledText;

    public bool ShowDisabledBadge => !Summary.Enabled;

    public string GeneratedText => Summary.GeneratedText;

    public string ReceivedText => Summary.ReceivedText;

    public string RevisionText => Summary.RevisionText;

    public string ChangedFieldsText => Summary.ChangedFieldsText;

    public string CapabilitiesText => Summary.CapabilitiesDeclared
        ? Summary.CapabilitySet.Any ? Summary.CapabilitySet.Text : "（路由器未声明能力）"
        : "（无控制面）";

    // ---- action gating: a button only exists when the action can really run --------------------

    /// <summary>Router refresh: the publisher runs that source's plugin for real.</summary>
    public bool ShowRefreshAction => Summary.CanRefresh;

    /// <summary>Router disable/enable.</summary>
    public bool ShowToggleEnabledAction => Summary.CanDisable;

    /// <summary>Router delete (only sources the publisher declares deletable, e.g. plan.smoke).</summary>
    public bool ShowDeleteAction => Summary.CanDelete;

    /// <summary>Router log tail.</summary>
    public bool ShowLogAction => Summary.CanLog;

    /// <summary>Local: publish now through the owning Service Unit's control endpoint.</summary>
    public bool ShowPublishNowAction => Summary.IsOwnedByThisTool;

    /// <summary>Local: stop/start the owning Service Unit.</summary>
    public bool ShowUnitToggleAction => Summary.IsOwnedByThisTool;

    /// <summary>Local UI Quota worker actions for the two sources it produces.</summary>
    public bool ShowUiQuotaActions => XbrdSourceOwnership.IsUiQuotaSource(SourceId);

    public bool HasCapabilityActions => ShowRefreshAction || ShowToggleEnabledAction || ShowDeleteAction || ShowLogAction;

    public bool HasAnyAction => HasCapabilityActions || ShowPublishNowAction || ShowUnitToggleAction || ShowUiQuotaActions;

    public string ToggleEnabledText => Summary.Enabled ? "停用" : "启用";

    public string UnitToggleText => _owner.IsUnitRunning(Summary.OwnerUnitId) ? "停服务" : "启服务";

    // ---- transient state ----------------------------------------------------------------------

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !_isBusy;

    public string BusyText
    {
        get => _busyText;
        set => SetProperty(ref _busyText, value);
    }

    public bool IsDeleteConfirming
    {
        get => _isDeleteConfirming;
        set => SetProperty(ref _isDeleteConfirming, value);
    }

    /// <summary>Last control evidence (exit code / duration / stdout tail) for this row.</summary>
    public string OutcomeText
    {
        get => _outcomeText;
        private set
        {
            if (SetProperty(ref _outcomeText, value))
            {
                OnPropertyChanged(nameof(HasOutcome));
            }
        }
    }

    public bool HasOutcome => _outcomeText.Length > 0;

    public bool OutcomeIsError
    {
        get => _outcomeIsError;
        private set
        {
            if (SetProperty(ref _outcomeIsError, value))
            {
                OnPropertyChanged(nameof(OutcomeIsOk));
                OnPropertyChanged(nameof(OutcomeLabel));
            }
        }
    }

    public bool OutcomeIsOk => !_outcomeIsError;

    public string OutcomeLabel => _outcomeIsError ? "动作失败" : "动作完成";

    public string LastLogText
    {
        get => _lastLogText;
        private set
        {
            if (SetProperty(ref _lastLogText, value))
            {
                OnPropertyChanged(nameof(HasLastLog));
            }
        }
    }

    public bool HasLastLog => _lastLogText.Length > 0;

    /// <summary>Re-evaluate the unit-dependent label after the owner refreshed Service Unit state.</summary>
    public void NotifyUnitStateChanged() => OnPropertyChanged(nameof(UnitToggleText));

    internal void SetOutcome(XbrdControlResult result) =>
        SetOutcome($"{result.HeadlineText}\n{result.EvidenceText}", result.IsFailure);

    internal void SetOutcome(XbrdPublishNowResult result) =>
        SetOutcome($"立即发布：{(result.Ok ? "成功" : "失败")}\n{result.DetailText}", !result.Ok);

    internal void SetOutcome(string text, bool isError)
    {
        OutcomeText = text.Trim();
        OutcomeIsError = isError;
    }

    internal void SetLog(string text) => LastLogText = text;

    /// <summary>Carry a previous row's evidence across a table rebuild (refresh replaces rows).</summary>
    internal void AdoptTransientState(string outcomeText, bool outcomeIsError, string logText, bool deleteConfirming)
    {
        if (outcomeText.Length > 0)
        {
            SetOutcome(outcomeText, outcomeIsError);
        }

        if (logText.Length > 0)
        {
            SetLog(logText);
        }

        if (deleteConfirming)
        {
            IsDeleteConfirming = true;
        }
    }

    internal void ClearDeleteConfirm() => IsDeleteConfirming = false;

    public ICommand RefreshCommand { get; }

    public ICommand ToggleEnabledCommand { get; }

    public ICommand ArmDeleteCommand { get; }

    public ICommand CancelDeleteCommand { get; }

    public ICommand ConfirmDeleteCommand { get; }

    public ICommand ShowSnapshotCommand { get; }

    public ICommand LoadLogCommand { get; }

    public ICommand PublishNowCommand { get; }

    public ICommand ToggleUnitCommand { get; }

    public ICommand CollectUiQuotaCommand { get; }

    public ICommand OpenQuotaWindowCommand { get; }
}

/// <summary>One line of the 「全部刷新」 result list.</summary>
public sealed record XbrdRefreshAllResult(string SourceId, bool Ok, string Text)
{
    public string Token => Ok ? "running" : "error";

    public bool IsReady => Ok;

    public bool IsError => !Ok;
}
