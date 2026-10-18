// The real row/presenter and SDK base are tested; no owner actions are invoked.
// This owner stand-in prevents UI construction, HostControl calls or process launches.
namespace Xbrd.Surface.ViewModels;

public sealed class XbrdSourcesViewModel
{
    private static Task NotUsed() => throw new InvalidOperationException("Owner action is outside this isolated presenter regression.");
    public Task RefreshSourceAsync(XbrdSourceRowViewModel row) => NotUsed();
    public Task ToggleSourceEnabledAsync(XbrdSourceRowViewModel row) => NotUsed();
    public Task DeleteSourceAsync(XbrdSourceRowViewModel row) => NotUsed();
    public void OpenDrawer(XbrdSourceRowViewModel row) => throw new InvalidOperationException("Not used by presenter tests.");
    public Task LoadSourceLogAsync(XbrdSourceRowViewModel row) => NotUsed();
    public Task PublishNowAsync(XbrdSourceRowViewModel row) => NotUsed();
    public Task ToggleSourceUnitAsync(XbrdSourceRowViewModel row) => NotUsed();
    public Task CollectUiQuotaAsync(XbrdSourceRowViewModel row) => NotUsed();
    public Task OpenQuotaWindowAsync(XbrdSourceRowViewModel row) => NotUsed();
    public bool IsUnitRunning(string? unit) => false;
}
