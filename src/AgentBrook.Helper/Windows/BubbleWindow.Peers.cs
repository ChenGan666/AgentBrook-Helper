using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AgentBrook.Helper.Windows;

// 伙伴气泡桥接：向 LanPeerBubbleController 提供圆心/展开边界/交互区判定。
public partial class BubbleWindow
{
    // ───────────────────────── 伙伴气泡：圆心 / 展开边界 / 交互区 ─────────────────────────
    /// <summary>气泡圆心在伙伴画布中的坐标（贴边平移由画布变换承担）。</summary>
    private Point PeerCenter()
    {
        var (cx, cy, _) = BubbleLayout();
        return new Point(cx, cy);
    }

    /// <summary>展开可用矩形（相对可见圆心，DIP）：窗口范围（向下可加高）∩ 圆心所在显示器工作区。</summary>
    private Rect PeerExpandBounds()
    {
        var (cx, cy, w) = BubbleLayout();
        const double m = LanPeerBubbleOptions.EdgeMargin;
        var vx = cx + _rootShift.X;
        var vy = cy + _rootShift.Y;
        var left = -vx + m;
        var right = w - vx - m;
        var top = -vy + m;
        var bottom = LanPeerBubbleOptions.ExpandedMaxBottom;
        // Windows 的 PixelPoint/WorkingArea 为物理像素需按缩放换算；macOS 为点单位（与贴边逻辑一致）
        var scale = OperatingSystem.IsMacOS() ? 1.0 : RenderScaling;
        var centerPx = new PixelPoint(Position.X + (int)Math.Round(vx * scale), Position.Y + (int)Math.Round(vy * scale));
        var screen = Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(centerPx)) ?? Screens.Primary;
        if (screen is not null)
        {
            var a = screen.WorkingArea;
            left = Math.Max(left, (a.X - centerPx.X) / scale + m);
            right = Math.Min(right, (a.Right - centerPx.X) / scale - m);
            top = Math.Max(top, (a.Y - centerPx.Y) / scale + m);
            bottom = Math.Min(bottom, (a.Bottom - centerPx.Y) / scale - m);
        }
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>指针是否仍在主球或伙伴气泡区域内。</summary>
    private bool IsPointerInPeerZone() =>
        this.FindControl<Panel>("Root")!.IsPointerOver || _peerLayer.IsPointerOver || _peerHoverLayer.IsPointerOver;
}
