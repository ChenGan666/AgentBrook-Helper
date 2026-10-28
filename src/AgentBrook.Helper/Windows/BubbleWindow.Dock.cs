using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentBrook.Helper.Windows;

// 磁吸贴边：气泡贴近左右上屏幕边时窗口贴齐、内容平移半隐；悬停滑出全部，离开延时收回。
public partial class BubbleWindow
{
    // ───────────────────────── 磁吸贴边：左右上半隐吸附，悬停滑出全部；下方弹回可视区 ─────────────────────────
    // macOS 专用（PixelPoint 与 Screen.WorkingArea 同为点单位；Windows 无紧凑模式不启用）。
    // 实现要点：窗口贴齐屏幕边、不越界（macOS 不允许窗口顶过菜单栏，越界也会被系统顶回）；
    // "半隐"靠平移窗口内的气泡内容让一半被窗口边裁掉；悬停只动平移量不动窗口，
    // 与紧凑/展开模式的位置补偿彻底解耦。
    private enum DockEdge { None, Left, Right, Top }
    private DockEdge _dockEdge = DockEdge.None;
    private bool _dockExpanded;
    private readonly TranslateTransform _rootShift = new();
    private readonly TranslateTransform _orbitShift = new();
    private readonly TranslateTransform _peerShift = new();
    private readonly TranslateTransform _peerHoverShift = new();
    private DispatcherTimer? _dockSlideTimer;
    private DispatcherTimer? _collapseTimer;   // 离开后延迟收回（防抖：边缘来回/瞬时离开不触发）
    private double _shiftFromX, _shiftFromY, _shiftToX, _shiftToY;
    private DateTime _slideStart;

    private const double BubbleVisualRadius = 25.2; // 气泡可见圆半径（主体 70 × 0.72 缩放 / 2）：贴边与吸附以它为准
    private const double DockSnapSlack = 20;        // 可见圆边缘贴近/越过屏幕边 20dip 内吸附

    /// <summary>贴边收起时的内容平移量：把圆心推到窗口对应边上（窗口边已贴齐屏幕边 → 恰好一半在屏内）。</summary>
    private (double X, double Y) CollapsedShift()
    {
        var (cx, cy, w) = BubbleLayout();
        return _dockEdge switch
        {
            DockEdge.Left => (-cx, 0),
            DockEdge.Right => (w - cx, 0),
            DockEdge.Top => (0, -cy),
            _ => (0, 0),
        };
    }

    /// <summary>悬停展开时的平移量：只滑出到气泡可见圆恰好完整贴着屏幕边（而非回到窗口原位）。
    /// 这样展开前被鼠标悬停的那半边始终被气泡覆盖——若滑回原位会从指针下移走，
    /// 触发"离开→收回→再进入→展开"的抖动循环（指针越靠近屏幕边越明显）。</summary>
    private (double X, double Y) ExpandedShift()
    {
        var (x, y) = CollapsedShift();
        return _dockEdge switch
        {
            DockEdge.Left => (x + BubbleVisualRadius, y),
            DockEdge.Right => (x - BubbleVisualRadius, y),
            DockEdge.Top => (x, y + BubbleVisualRadius),
            _ => (0, 0),
        };
    }

    private void SetShift(double x, double y)
    {
        _rootShift.X = x;
        _rootShift.Y = y;
        _orbitShift.X = x;
        _orbitShift.Y = y;
        _peerShift.X = x;
        _peerShift.Y = y;
        _peerHoverShift.X = x;
        _peerHoverShift.Y = y;
        UpdateClickThroughRegion();   // 半隐平移后可见半圆位置改变，命中区同步（否则 Windows 悬停穿透无法展开）
    }

    /// <summary>把窗口贴齐当前吸附边（另一轴保持不动）。</summary>
    private void PlaceWindowFlush(PixelRect area)
    {
        var (_, _, w) = BubbleLayout();
        var p = Position;
        Position = _dockEdge switch
        {
            DockEdge.Left => new PixelPoint(area.X, p.Y),
            DockEdge.Right => new PixelPoint(area.Right - (int)w, p.Y),
            DockEdge.Top => new PixelPoint(p.X, area.Y),
            _ => p,
        };
    }

    /// <summary>拖拽结束/位置恢复后调用：以用户实际看到的圆心判定，左右上吸附半隐；
    /// 未吸附时把越界气泡拉回完全可视（下方永不半隐，贴近下边自动弹回）。</summary>
    private void ApplyEdgeMagnet()
    {
        StopDockSlide();
        var raw = GetBubbleCenter();
        var visual = new PixelPoint(raw.X + (int)_rootShift.X, raw.Y + (int)_rootShift.Y);   // 含当前平移
        var screen = ScreenOf(visual);
        if (screen is null)
        {
            return;
        }
        var area = screen.WorkingArea;
        DockEdge edge;
        if (visual.X - BubbleVisualRadius <= area.X + DockSnapSlack)
        {
            edge = DockEdge.Left;
        }
        else if (visual.X + BubbleVisualRadius >= area.Right - DockSnapSlack)
        {
            edge = DockEdge.Right;
        }
        else if (Position.Y - area.Y <= DockSnapSlack)
        {
            // 上边特殊：macOS 不允许窗口顶越过菜单栏（会被 constrainFrameRect 顶回），
            // 用户把气泡向上推到底时窗口顶必然停在工作区上缘——以此判定"推向上边"；
            // 半隐靠内容上移被窗口上缘裁掉实现
            edge = DockEdge.Top;
        }
        else
        {
            edge = DockEdge.None;
        }
        _dockEdge = edge;
        _dockExpanded = false;
        if (edge == DockEdge.None)
        {
            // 未吸附：窗口对齐到用户看到的位置并夹回可视区，内容平移归零（视觉连续，不跳）
            var c = visual.WithX(Math.Clamp(visual.X, (int)(area.X + BubbleHalf + 4), (int)(area.Right - BubbleHalf - 4)));
            c = c.WithY(Math.Clamp(c.Y, (int)(area.Y + BubbleHalf + 4), (int)(area.Bottom - BubbleHalf - 4)));
            MoveBubbleCenter(c);
            SetShift(0, 0);
            return;
        }
        MoveBubbleCenter(visual);
        PlaceWindowFlush(area);
        var (sx, sy) = CollapsedShift();
        SetShift(sx, sy);
    }

    /// <summary>紧凑/展开模式切换后：窗口重新贴齐、平移量按新模式重算（补偿只保证圆心不动，贴边需要重对齐）。</summary>
    private void ReapplyDock()
    {
        if (_dockEdge == DockEdge.None)
        {
            return;
        }
        var screen = ScreenOf(GetBubbleCenter());
        if (screen is null)
        {
            return;
        }
        PlaceWindowFlush(screen.WorkingArea);
        var (sx, sy) = _dockExpanded ? ExpandedShift() : CollapsedShift();
        SetShift(sx, sy);
    }

    /// <summary>悬停展开/收起：贴边状态下鼠标滑过把气泡滑入屏内全部可见（向边缘反方向平移），离开滑回半隐。</summary>
    private void SetDockExpanded(bool expand)
    {
        if (_dockEdge == DockEdge.None || _dockExpanded == expand)
        {
            return;
        }
        _dockExpanded = expand;
        var (tx, ty) = expand ? ExpandedShift() : CollapsedShift();
        SlideShiftTo(tx, ty);
    }

    /// <summary>内容平移滑动动画（easeOutCubic，~160ms）。</summary>
    private void SlideShiftTo(double x, double y)
    {
        _shiftFromX = _rootShift.X;
        _shiftFromY = _rootShift.Y;
        _shiftToX = x;
        _shiftToY = y;
        _slideStart = DateTime.UtcNow;
        _dockSlideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dockSlideTimer.Tick -= SlideTick;
        _dockSlideTimer.Tick += SlideTick;
        if (!_dockSlideTimer.IsEnabled)
        {
            _dockSlideTimer.Start();
        }
    }

    private void SlideTick(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _slideStart).TotalMilliseconds / 160.0;
        if (t >= 1)
        {
            SetShift(_shiftToX, _shiftToY);
            _dockSlideTimer!.Stop();
            return;
        }
        var k = 1 - Math.Pow(1 - t, 3);
        SetShift(_shiftFromX + (_shiftToX - _shiftFromX) * k, _shiftFromY + (_shiftToY - _shiftFromY) * k);
    }

    /// <summary>离开气泡 220ms 后再收回；期间再进入即取消（避免边缘来回移动时反复缩回/滑出）。</summary>
    private void ScheduleDockCollapse()
    {
        if (_dockEdge == DockEdge.None || !_dockExpanded)
        {
            return;
        }
        _collapseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _collapseTimer.Tick -= CollapseTick;
        _collapseTimer.Tick += CollapseTick;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void CollapseTick(object? sender, EventArgs e)
    {
        _collapseTimer!.Stop();
        if (_toastPanel.Children.Count == 0 && !IsPointerInPeerZone())
        {
            SetDockExpanded(false);   // 离开且无提示卡时滑回半隐
        }
    }

    private void StopDockSlide()
    {
        _dockSlideTimer?.Stop();
    }
}
