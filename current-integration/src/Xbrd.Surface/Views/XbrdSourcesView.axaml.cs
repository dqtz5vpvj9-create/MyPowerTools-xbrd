using Avalonia;
using Avalonia.Controls;
using Xbrd.Surface.ViewModels;

namespace Xbrd.Surface.Views;

/// <summary>
/// Code-behind for the 「来源与配额」 Surface. The view only owns lifecycle wiring: the auto-refresh
/// timer lives in the view model and is released when the surface leaves the visual tree (the Shell
/// recreates the surface per navigation and disposes the data context).
/// </summary>
public partial class XbrdSourcesView : UserControl
{
    public XbrdSourcesView()
    {
        InitializeComponent();
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        // On macOS a hidden/minimised window must keep pages, bindings and command state alive
        // (AGENTS.md low-power policy), so only dispose when the surface really goes away.
        if (OperatingSystem.IsMacOS() && eventArgs.RootVisual is Window window &&
            (!window.IsVisible || window.WindowState == WindowState.Minimized))
        {
            return;
        }

        (DataContext as IDisposable)?.Dispose();
    }
}
