using System.Windows.Input;
using MyPowerTools.AvaloniaSdk;
using Xbrd.Surface.Services;

namespace Xbrd.Surface.ViewModels;

/// <summary>One source row: status pill, TTL budget, error text and the inline row actions.</summary>
public sealed class XbrdSourceRowViewModel : MptObservableViewModel
{
    private readonly XbrdSourcesViewModel _owner;
    private bool _isDetailOpen;
    private string _manageButtonText;

    public XbrdSourceRowViewModel(XbrdSourceSummary summary, XbrdSourcesViewModel owner)
    {
        Summary = summary;
        _owner = owner;
        _manageButtonText = summary.IsOwnedByThisTool ? "停用服务" : "停用/注销";

        RefreshCommand = new MptAsyncRelayCommand(
            () => _owner.RefreshSourceAsync(this),
            operationName: $"xbrd.source.refresh:{summary.SourceId}");
        ManageCommand = new MptAsyncRelayCommand(
            () => _owner.ManageSourceUnitAsync(this),
            operationName: $"xbrd.source.manage:{summary.SourceId}");
        ShowSnapshotCommand = new MptAsyncRelayCommand(
            () =>
            {
                IsDetailOpen = !IsDetailOpen;
                _owner.SelectSource(IsDetailOpen ? this : null);
                return Task.CompletedTask;
            },
            operationName: $"xbrd.source.snapshot:{summary.SourceId}");
    }

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

    public string GeneratedText => Summary.GeneratedText;

    public string AgeText => Summary.AgeText;

    public string RevisionText => Summary.RevisionText;

    public string ChangedFieldsText => Summary.ChangedFieldsText;

    public string ErrorText => Summary.ErrorText;

    public bool HasError => Summary.HasError;

    public string OwnerText => Summary.OwnerText;

    public string ManageHint => Summary.ManageHint;

    public string ReceivedText => Summary.ReceivedText;

    public bool CanManage => Summary.IsOwnedByThisTool;

    public string ManageButtonText
    {
        get => _manageButtonText;
        set => SetProperty(ref _manageButtonText, value);
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set
        {
            if (SetProperty(ref _isDetailOpen, value))
            {
                OnPropertyChanged(nameof(SnapshotButtonText));
            }
        }
    }

    public string SnapshotButtonText => IsDetailOpen ? "收起快照" : "查看快照";

    public ICommand RefreshCommand { get; }

    public ICommand ManageCommand { get; }

    public ICommand ShowSnapshotCommand { get; }
}
