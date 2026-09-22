using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.UI;
using Xbrd.Surface.ViewModels;
using Xbrd.Surface.Views;

namespace Xbrd.Surface;

/// <summary>
/// 来源与配额 Tab 的 dotnet Surface 工厂（routeId <c>sources</c>）。
/// 由 Shell 的 <c>DotnetSurfaceLoader</c> 依据 tool.json → routes[sources].surface 的
/// assembly/type 字段加载：assembly <c>surface/Xbrd.Surface.dll</c>，类型
/// <c>Xbrd.Surface.XbrdSurfaceFactory</c>（CONTRACT.md §2）。
///
/// 契约边界：
///  · 只读发布器（GET /health、GET /api/v1/sources），不写路由器；
///  · 本机动作（采集 UI 配额、打开配额验证窗口、重启 Service Unit、查看日志）用
///    UseShellExecute=false + CreateNoWindow=true 启动，绝不弹控制台窗口；
///  · 不读取密钥值，不新增后端，不自带主题/窗口。
/// </summary>
public sealed class XbrdSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        Info(context, $"Xbrd.Surface 加载中（tool={context.ToolId}，route={context.RouteId}）。");

        var host = new ContentControl { Content = CreateLoadingView() };
        _ = PopulateAsync(host, context);
        return host;
    }

    private static async Task PopulateAsync(ContentControl host, MptAvaloniaSurfaceContext context)
    {
        try
        {
            var viewModel = new XbrdSourcesViewModel(context);
            await viewModel.InitializeAsync();

            SetContent(host, new XbrdSourcesView { DataContext = viewModel });
            Info(
                context,
                $"Xbrd.Surface 已就绪：聚合 {viewModel.AggregateHeadline}；{viewModel.SourcesSummaryText}；{viewModel.UnitsSummaryText}。");
        }
        catch (Exception ex)
        {
            Info(context, $"Xbrd.Surface 加载失败：{ex.Message}");
            SetContent(host, CreateFailureView(host, context, ex));
        }
    }

    private static void SetContent(ContentControl host, Control content)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            host.Content = content;
            return;
        }

        Dispatcher.UIThread.Post(() => host.Content = content);
    }

    private static Control CreateLoadingView() =>
        new Border
        {
            Padding = MptThemeTokens.PageMargin,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "XBRD 来源与配额",
                        FontSize = MptThemeTokens.FontSizeTitle,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock { Text = "正在读取发布器、来源清单与两个采集服务…" },
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Width = 260,
                        HorizontalAlignment = HorizontalAlignment.Left
                    }
                }
            }
        };

    private static Control CreateFailureView(
        ContentControl host,
        MptAvaloniaSurfaceContext context,
        Exception exception)
    {
        var retry = new Button
        {
            Content = "重试",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        retry.Click += (_, _) =>
        {
            host.Content = CreateLoadingView();
            _ = PopulateAsync(host, context);
        };

        var detail = new SelectableTextBlock
        {
            Text = exception.GetBaseException().Message,
            TextWrapping = TextWrapping.Wrap
        };

        return new Border
        {
            Padding = MptThemeTokens.PageMargin,
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "XBRD 来源与配额暂时无法加载",
                        FontSize = MptThemeTokens.FontSizeSection,
                        FontWeight = FontWeight.SemiBold
                    },
                    detail,
                    retry
                }
            }
        };
    }

    private static void Info(MptAvaloniaSurfaceContext context, string message)
    {
        context.Log(new MptSurfaceLogEntry("info", message, DateTimeOffset.Now));
    }
}
