using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;

namespace AgentBrook.Helper.Windows;

// 窗口位置与布局：气泡在窗口内的偏移几何、位置持久化、macOS 紧凑模式、圆心/屏幕换算、高度自适应。
public partial class BubbleWindow
{
    // ── 气泡布局尺寸常量（唯一来源：窗口/气泡尺寸与在窗口内的偏移）──
    private const double WindowWidth = 240.0;                                   // 常规窗口宽（高自适应）
    private const double CompactWindowSize = 120.0;                             // macOS 紧凑窗口宽
    private const double BubbleSize = 96.0;                                     // 气泡直径
    private const double BubbleHalf = BubbleSize / 2;                           // 48：圆半径（可视区夹取）
    private const double BubbleOffsetX = (WindowWidth - BubbleSize) / 2;        // 72：气泡在窗口内 X 偏移
    private const double BubbleOffsetY = 24.0;                                  // 气泡在窗口内顶部下移
    private const double CompactBubbleOffsetX = (CompactWindowSize - BubbleSize) / 2;  // 12
    private const double CompactBubbleOffsetY = 12.0;
    // 派生：圆心在窗口内位置 = (Width/2, OffsetY + Half)；紧凑/常规模式位置补偿 Δ = (60, 12)
    private const double CompactShiftDx = BubbleOffsetX - CompactBubbleOffsetX; // 60
    private const double CompactShiftDy = BubbleOffsetY - CompactBubbleOffsetY; // 12

    private void PlaceBubble()
    {
        var saved = UiPrefs.Load<BubblePos>("bubble-pos.json");
        if (saved is not null)
        {
            // V=2 存气泡圆心；V=1 旧格式存窗口原点（旧文件不再做坐标系换算，直接当原点用）
            var (cx, cy) = saved.V >= 2
                ? (saved.X, saved.Y)
                : (saved.X + (int)BubbleOffsetX + (int)BubbleHalf, saved.Y + (int)BubbleOffsetY + (int)BubbleHalf);
            // 可见性保护：气泡圆心必须落在任一显示器工作区内，否则回落默认位置
            var bubbleCenter = new PixelPoint(cx, cy);
            if (Screens.All.Any(s => s.WorkingArea.Contains(bubbleCenter)))
            {
                Position = new PixelPoint((int)(cx - BubbleOffsetX - BubbleHalf), (int)(cy - BubbleOffsetY - BubbleHalf));
                ApplyEdgeMagnet();   // 恢复位置贴近边缘：按需自动贴边半隐
                return;
            }
        }

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }
        var area = screen.WorkingArea;
        // 以气泡（96 宽）为基准放主屏右上角，再换算窗口原点
        var x = area.X + area.Width - BubbleSize - 24 - BubbleOffsetX;
        var y = area.Y + 108 - BubbleOffsetY;
        // 兜底 clamp：保证气泡圆完整落在主屏工作区内（下方提示区允许随屏幕裁剪）
        x = Math.Max(area.X + 8 - BubbleOffsetX, Math.Min(x, area.X + area.Width - BubbleSize - 8 - BubbleOffsetX));
        y = Math.Max(area.Y + 8 - BubbleOffsetY, Math.Min(y, area.Y + area.Height - BubbleSize - 8 - BubbleOffsetY));
        Position = new PixelPoint((int)x, (int)y);
        ApplyEdgeMagnet();   // 默认位置靠右上角：按需自动贴边半隐
    }

    /// <summary>把当前气泡位置写入偏好（拖动结束/退出时调用）。V=2：存气泡圆心。</summary>
    private void SavePosition()
    {
        var x = Position.X;
        var y = Position.Y;
        if (OperatingSystem.IsMacOS() && _compactMode)
        {
            // 把紧凑窗口位置转换为等效的非紧凑窗口位置，保持 PlaceBubble 兼容
            // （macOS 的 PixelPoint 为点单位，同上不乘 RenderScaling）
            x -= (int)CompactShiftDx;
            y -= (int)CompactShiftDy;
        }
        UiPrefs.Save("bubble-pos.json", new BubblePos(
            x + (int)BubbleOffsetX + (int)BubbleHalf, y + (int)BubbleOffsetY + (int)BubbleHalf, 2));
    }

    /// <summary>删除位置偏好并回到默认位置（设置窗口的重置按钮调用）。</summary>
    public void ResetPositionToDefault()
    {
        UiPrefs.Delete("bubble-pos.json");
        PlaceBubble();
    }

    // ───────────────────────── macOS 紧凑模式：Idle 时窗口缩小到气泡大小 ─────────────────────────
    private bool _compactMode;   // macOS 专用：当前是否处于气泡大小的紧凑窗口

    /// <summary>
    /// macOS 透明窗口没有 SetWindowRgn，Idle 且没有轨道/提示卡时把窗口缩小到气泡区域，
    /// 避免 240×120 的矩形透明区拦截桌面点击并造成视觉占用。
    /// 位置补偿保证切换前后气泡在屏幕上的位置不变。
    /// </summary>
    private void UpdateWindowLayoutMode()
    {
        if (!OperatingSystem.IsMacOS()) return;
        StopDockSlide();   // 紧凑/展开切换的位置补偿与滑动动画互斥，先停
        var needCompact = _toastPanel.Children.Count == 0 && _orbit.Count == 0 && !(_peerBubbles?.NeedsFullWindow ?? false);
        if (needCompact == _compactMode || !IsVisible) return;

        // 非紧凑/紧凑模式位置补偿：圆心不动，偏移差 (CompactShiftDx, CompactShiftDy)
        var root = this.FindControl<Panel>("Root")!;
        var orbit = this.FindControl<Canvas>("OrbitLayer")!;
        if (needCompact)
        {
            // 左右边距必须对称：控件是 HorizontalAlignment=Center，非对称边距会把气泡中心
            // 推到 (66,66) 而非下方位置补偿假设的 (60,60)，导致切模式时气泡跳动、贴边图标多裁 6px
            root.Margin = new Thickness(CompactBubbleOffsetX, CompactBubbleOffsetY, CompactBubbleOffsetX, CompactBubbleOffsetY);
            orbit.Margin = new Thickness(CompactBubbleOffsetX, CompactBubbleOffsetY, CompactBubbleOffsetX, CompactBubbleOffsetY);
            // macOS 的 PixelPoint 与 Screen.Bounds 同为点（DIP）单位，不乘 RenderScaling——
            // 乘了会把补偿量放大一倍，轨道图标出现/消失时气泡整体跳动 60pt
            Position = new PixelPoint(Position.X + (int)CompactShiftDx, Position.Y + (int)CompactShiftDy);
            Width = CompactWindowSize;
            Height = CompactWindowSize;
            SizeToContent = SizeToContent.Manual;
            _compactMode = true;
        }
        else
        {
            root.Margin = new Thickness(0, BubbleOffsetY, 0, 0);
            orbit.Margin = new Thickness(0, BubbleOffsetY, 0, 0);
            Position = new PixelPoint(Position.X - (int)CompactShiftDx, Position.Y - (int)CompactShiftDy);
            Width = WindowWidth;
            ClearValue(HeightProperty);
            SizeToContent = SizeToContent.Height;
            _compactMode = false;
        }
        ReapplyDock();   // 贴边中：窗口按新模式重新贴齐、平移量重算
        _peerBubbles?.Reposition();   // 圆心随模式变化，伙伴头像同步坐标
    }

    /// <summary>
    /// 窗口高度自适应守护：Avalonia 在原生拖动循环中收到尺寸变化时会把它当作用户调整，
    /// 自动关闭 SizeToContent 并固定当时的高度（头像展开/收拢会改变窗口高度），之后提示卡就会被裁掉。
    /// 非紧凑模式下发现被关闭即恢复。
    /// </summary>
    private void EnsureAutoHeight()
    {
        if (_compactMode || SizeToContent == SizeToContent.Height)
        {
            return;
        }
        ClearValue(HeightProperty);
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>气泡圆心在窗口内的偏移与窗口宽度（随紧凑/展开模式）。</summary>
    private (double Cx, double Cy, double W) BubbleLayout() =>
        _compactMode
            ? (CompactWindowSize / 2, CompactBubbleOffsetY + BubbleHalf, CompactWindowSize)
            : (WindowWidth / 2, BubbleOffsetY + BubbleHalf, WindowWidth);

    /// <summary>未平移时的气泡圆心（由窗口位置换算）。</summary>
    private PixelPoint GetBubbleCenter()
    {
        var (cx, cy, _) = BubbleLayout();
        return new PixelPoint(Position.X + (int)cx, Position.Y + (int)cy);
    }

    private void MoveBubbleCenter(PixelPoint center)
    {
        var (cx, cy, _) = BubbleLayout();
        Position = new PixelPoint(center.X - (int)cx, center.Y - (int)cy);
    }

    private Avalonia.Platform.Screen? ScreenOf(PixelPoint center) =>
        Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(center)) ?? Screens.Primary;
}
