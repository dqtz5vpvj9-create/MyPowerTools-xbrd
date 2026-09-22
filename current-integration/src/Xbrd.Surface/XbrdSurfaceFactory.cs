using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyPowerTools.AvaloniaSdk;

namespace Xbrd.Surface;

/// <summary>
/// 来源与配额 Tab 的 dotnet Surface 工厂。
/// 由 Shell 的 DotnetSurfaceLoader 依据 route 的 assembly/type 字段加载
/// （tool.json → routes[sources].surface）。
///
/// 本文件是 t0 骨架占位：只证明混合 route（web + dotnet）能正常分发。
/// 真正的来源清单（9 个来源的状态/TTL/错误、采集器与 Service Unit 状态、时间线）
/// 由所有者 B 在此目录内实现，契约见仓库根 CONTRACT.md。
/// </summary>
public sealed class XbrdSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        context.Log(new MptSurfaceLogEntry(
            "info",
            $"Xbrd.Surface placeholder loaded (tool={context.ToolId}, route={context.RouteId}).",
            DateTimeOffset.Now));

        return new Border
        {
            Padding = new Thickness(28),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "XBRD 来源与配额",
                        FontSize = 26,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = "Surface 骨架已加载（t0 占位）。来源清单、TTL 健康、采集器与常驻服务状态由所有者 B 实现。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = $"toolId = {context.ToolId} · routeId = {context.RouteId}",
                        Opacity = 0.7
                    }
                }
            }
        };
    }
}
