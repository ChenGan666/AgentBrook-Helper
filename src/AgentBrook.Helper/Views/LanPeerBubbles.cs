using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Windows;
using AgentBrook.A2A.Core.Peers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentBrook.Helper.Views;

/// <summary>
/// 伙伴气泡（LAN Peer Bubbles）可调参数：尺寸、布局、动画时长集中于此，不散落在各处。
/// 所有长度单位均为 DIP（逻辑像素），坐标相对气泡圆心。
/// </summary>
internal static class LanPeerBubbleOptions
{
    /// <summary>收拢头像直径。</summary>
    public const double CollapsedSize = 36;
    /// <summary>展开头像直径。</summary>
    public const double ExpandedSize = 52;
    /// <summary>收拢弧半径（圆心 → 头像中心）：头像内缘距圆心 12dip，不遮挡中心图标（半径约 10dip）。</summary>
    public const double CollapsedArcRadius = 30;
    /// <summary>收拢时相邻头像重叠比例（相对头像直径）。</summary>
    public const double CollapsedOverlap = 0.38;
    /// <summary>收拢弧最大张角（度）。</summary>
    public const double CollapsedMaxSpanDeg = 140;
    /// <summary>收拢状态最多直接展示的头像数（超出时末尾为 +N 气泡）；头像较大，保证主体清晰取 3。</summary>
    public const int CollapsedMaxVisible = 3;
    /// <summary>展开第一环半径：光晕半径 30 + 头像半径 26 + 间隙 4。</summary>
    public const double ExpandedFirstRingRadius = 60;
    /// <summary>展开同环相邻头像间距。</summary>
    public const double ExpandedGap = 6;
    /// <summary>展开相邻环之间的间距。</summary>
    public const double ExpandedRingGap = 6;
    /// <summary>展开最多环数（超出容量时末格为 +N）。</summary>
    public const int ExpandedMaxRings = 3;
    /// <summary>展开弧段两端距水平线的最小角（度）。</summary>
    public const int ExpandedArcEdgeDeg = 10;
    /// <summary>展开区域向下最大延伸（圆心以下），窗口按需加高。</summary>
    public const double ExpandedMaxBottom = 150;
    /// <summary>距窗口/屏幕边缘的安全边距。</summary>
    public const double EdgeMargin = 2;
    public const int ExpandDurationMs = 200;
    public const int CollapseDurationMs = 180;
    public const int FadeDurationMs = 220;
    public const int StaggerMs = 20;
    public const int CollapseDelayMs = 250;
    public const double HoverScale = 1.08;
    /// <summary>头像按下后移动超过该距离视为拖动气泡，而非点击。</summary>
    public const double DragThreshold = 4;
    /// <summary>信标兜底在线窗口：Presence 30 秒未跳即过期，信标兜底再给 5 秒余量取 35 秒。</summary>
    public static readonly TimeSpan BeaconAliveWindow = TimeSpan.FromSeconds(35);
    /// <summary>超时离线检查间隔（仅存在在线伙伴时运行）。</summary>
    public static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromSeconds(5);
    /// <summary>A2A Changed 事件合并窗口（信标每 2 秒到达，避免频繁读库）。</summary>
    public static readonly TimeSpan ChangeCoalesce = TimeSpan.FromMilliseconds(300);
}

/// <summary>伙伴气泡展示模型（以 NodeId 为唯一标识）。</summary>
internal sealed record LanPeerInfo(string NodeId, string DisplayName, string? Icon, string Status);

/// <summary>
/// 在线伙伴数据适配器：复用 A2A 现有的 Peer 存储与 Presence 心跳，不重复实现发现协议。
/// 只输出已配对（Trusted）且在线的节点，按 NodeId 去重，排除本机。
/// </summary>
internal sealed class LanPeerSource : IDisposable
{
    private readonly A2AExtensionController _a2a;
    private readonly DispatcherTimer _coalesceTimer;
    private readonly DispatcherTimer _expiryTimer;
    private bool _loading;
    private bool _reloadPending;
    private bool _disposed;

    public LanPeerSource(A2AExtensionController a2a)
    {
        _a2a = a2a;
        _coalesceTimer = new DispatcherTimer { Interval = LanPeerBubbleOptions.ChangeCoalesce };
        _coalesceTimer.Tick += (_, _) =>
        {
            _coalesceTimer.Stop();
            Reload();
        };
        _expiryTimer = new DispatcherTimer { Interval = LanPeerBubbleOptions.ExpiryCheckInterval };
        _expiryTimer.Tick += (_, _) => Reload();
        _a2a.Changed += OnA2AChanged;
        Reload();
    }

    /// <summary>当前在线伙伴（只读快照，UI 线程访问）。</summary>
    public IReadOnlyList<LanPeerInfo> Current { get; private set; } = [];

    /// <summary>在线列表变化（UI 线程触发）。</summary>
    public event Action? PeersChanged;

    private void OnA2AChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && !_coalesceTimer.IsEnabled)
            {
                _coalesceTimer.Start();
            }
        });
    }

    private async void Reload()
    {
        if (_disposed)
        {
            return;
        }
        if (_loading)
        {
            _reloadPending = true;
            return;
        }
        _loading = true;
        try
        {
            var list = await Task.Run(LoadAsync);
            if (_disposed)
            {
                return;
            }
            if (!list.SequenceEqual(Current))
            {
                Current = list;
                PeersChanged?.Invoke();
            }
            if (Current.Count > 0)
            {
                if (!_expiryTimer.IsEnabled)
                {
                    _expiryTimer.Start();
                }
            }
            else
            {
                _expiryTimer.Stop();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[peer-bubbles] load failed: {ex.Message}");
        }
        finally
        {
            _loading = false;
            if (_reloadPending && !_disposed)
            {
                _reloadPending = false;
                Reload();
            }
        }
    }

    private async Task<List<LanPeerInfo>> LoadAsync()
    {
        var host = _a2a.Host;
        if (host is null)
        {
            return [];
        }
        var self = _a2a.NodeId;
        var peers = await host.Store.GetPeersAsync();
        var presences = host.Presence.GetRemotePresences();
        var beaconCutoff = DateTimeOffset.UtcNow - LanPeerBubbleOptions.BeaconAliveWindow;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LanPeerInfo>();
        foreach (var peer in peers
                     .OrderBy(p => p.DisplayName ?? p.NodeId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.NodeId, StringComparer.Ordinal))
        {
            if (peer.TrustState != PeerTrustState.Trusted
                || string.IsNullOrWhiteSpace(peer.NodeId)
                || string.Equals(peer.NodeId, self, StringComparison.Ordinal)
                || !seen.Add(peer.NodeId))
            {
                continue;
            }
            presences.TryGetValue(peer.NodeId, out var presence);
            var online = presence is not null
                ? presence.Availability != PresenceAvailability.Offline
                : (peer.LastSeenAt ?? DateTimeOffset.MinValue) > beaconCutoff;
            if (!online)
            {
                continue;
            }
            var name = string.IsNullOrWhiteSpace(peer.DisplayName)
                ? peer.NodeId[..Math.Min(10, peer.NodeId.Length)]
                : peer.DisplayName!;
            var icon = string.IsNullOrWhiteSpace(peer.Icon) ? null : peer.Icon;
            result.Add(new LanPeerInfo(peer.NodeId, name, icon, StatusText(presence)));
        }
        return result;
    }

    private static string StatusText(PresenceSnapshot? presence)
    {
        if (presence is null)
        {
            return I18n.T("peer.state.online");
        }
        return presence.Availability switch
        {
            PresenceAvailability.Away => I18n.T("peer.state.away"),
            PresenceAvailability.Draining => I18n.T("peer.state.draining"),
            _ => presence.WorkState switch
            {
                WorkState.Busy => I18n.T("peer.state.busy"),
                WorkState.AwaitingUser => I18n.T("peer.state.awaiting"),
                WorkState.Unavailable => I18n.T("peer.state.unavailable"),
                _ => I18n.T("peer.state.online"),
            },
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _a2a.Changed -= OnA2AChanged;
        _coalesceTimer.Stop();
        _expiryTimer.Stop();
    }
}

/// <summary>伙伴气泡布局计算：通用算法，按人数、头像尺寸与可用空间动态计算（坐标相对圆心，y 向下）。</summary>
internal static class LanPeerLayout
{
    /// <summary>收拢布局：沿主体下半部弧线叠放，间距随数量压缩，左右对称。</summary>
    public static List<Point> Collapsed(int count)
    {
        var result = new List<Point>(count);
        if (count <= 0)
        {
            return result;
        }
        var r = LanPeerBubbleOptions.CollapsedArcRadius;
        var stepMax = LanPeerBubbleOptions.CollapsedSize * (1 - LanPeerBubbleOptions.CollapsedOverlap) / r;
        var maxSpan = LanPeerBubbleOptions.CollapsedMaxSpanDeg * Math.PI / 180;
        var step = count > 1 ? Math.Min(stepMax, maxSpan / (count - 1)) : 0;
        var start = Math.PI / 2 + step * (count - 1) / 2;   // 从左到右
        for (var i = 0; i < count; i++)
        {
            var angle = start - i * step;
            result.Add(new Point(r * Math.Cos(angle), r * Math.Sin(angle)));
        }
        return result;
    }

    /// <summary>
    /// 展开布局：以光晕外侧为第一环的多环弧形队列。每环可用弧段按可用矩形（窗口 ∩ 屏幕工作区）裁剪；
    /// 下方空间不足时尝试向上展开，取容量更大的方向。返回的槽位数可能少于 count（由调用方显示 +N）。
    /// </summary>
    public static (List<Point> Slots, bool Upward) Expanded(int count, Rect bounds)
    {
        var down = Plan(count, bounds, upward: false);
        if (down.Count >= count)
        {
            return (down, false);
        }
        var up = Plan(count, bounds, upward: true);
        return up.Count > down.Count ? (up, true) : (down, false);
    }

    private static List<Point> Plan(int count, Rect bounds, bool upward)
    {
        var size = LanPeerBubbleOptions.ExpandedSize;
        var half = size / 2;
        var edge = LanPeerBubbleOptions.ExpandedArcEdgeDeg;
        var result = new List<Point>();
        for (var ring = 0; ring < LanPeerBubbleOptions.ExpandedMaxRings && result.Count < count; ring++)
        {
            var r = LanPeerBubbleOptions.ExpandedFirstRingRadius + ring * (size + LanPeerBubbleOptions.ExpandedRingGap);
            var step = (size + LanPeerBubbleOptions.ExpandedGap) / r;

            // 1° 采样求该环最长的连续可用弧段
            int bestA = 0, bestB = -1, runStart = -1;
            for (var d = edge; d <= 180 - edge + 1; d++)
            {
                var ok = d <= 180 - edge && Fits(d, r, half, bounds, upward);
                if (ok && runStart < 0)
                {
                    runStart = d;
                }
                else if (!ok && runStart >= 0)
                {
                    if (d - 1 - runStart > bestB - bestA)
                    {
                        bestA = runStart;
                        bestB = d - 1;
                    }
                    runStart = -1;
                }
            }
            if (bestB < bestA)
            {
                continue;
            }
            var a = bestA * Math.PI / 180;
            var b = bestB * Math.PI / 180;
            var capacity = (int)Math.Floor((b - a) / step + 1e-6) + 1;
            var m = Math.Min(capacity, count - result.Count);
            var span = (m - 1) * step;
            var center = Math.Max(a + span / 2, Math.Min(Math.PI / 2, b - span / 2));
            for (var i = 0; i < m; i++)
            {
                var angle = center + span / 2 - i * step;   // 从左到右
                var y = r * Math.Sin(angle);
                result.Add(new Point(r * Math.Cos(angle), upward ? -y : y));
            }
        }
        return result;
    }

    private static bool Fits(int degree, double r, double half, Rect b, bool upward)
    {
        var angle = degree * Math.PI / 180;
        var x = r * Math.Cos(angle);
        var y = r * Math.Sin(angle);
        if (upward)
        {
            y = -y;
        }
        return x - half >= b.Left && x + half <= b.Right && y - half >= b.Top && y + half <= b.Bottom;
    }
}

/// <summary>单个伙伴头像（或 +N 数量气泡）：负责外观、悬停、提示、点击/拖动判定与自身插值状态；不决定整体展开/收拢。</summary>
internal sealed class LanPeerAvatar : Border
{
    private const double BaseSize = LanPeerBubbleOptions.ExpandedSize;
    private static readonly IBrush MoreBackground = new SolidColorBrush(Color.Parse("#6f8fb8"));

    private readonly ScaleTransform _scale = new();
    private readonly TextBlock _text;
    private readonly Avalonia.Controls.Shapes.Path _person;
    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private bool _pressed;
    private bool _hover;
    private Point _center;
    private string? _tip;
    private bool _tipEnabled;

    private double _fromX, _fromY, _fromSize, _fromAlpha;
    private double _toX, _toY, _toSize, _toAlpha;
    private DateTime _animStart;
    private double _animDuration;

    public LanPeerAvatar(string key, bool isMore)
    {
        Key = key;
        IsMore = isMore;
        Width = BaseSize;
        Height = BaseSize;
        Background = Brushes.Transparent;
        CornerRadius = new CornerRadius(BaseSize / 2);
        BoxShadow = BoxShadows.Parse("0 1 3 0 #40000000");
        ClipToBounds = true;
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = _scale;
        Cursor = new Cursor(StandardCursorType.Hand);

        _text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            FontSize = isMore ? 11 : 14,
            FontWeight = isMore ? FontWeight.SemiBold : FontWeight.Normal,
        };
        _person = new Avalonia.Controls.Shapes.Path
        {
            Fill = Brushes.White,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(12),
            IsVisible = false,
        };
        if (Application.Current is { } app && app.TryFindResource("Icon.Person", out var res) && res is Geometry geo)
        {
            _person.Data = geo;
        }
        var grid = new Grid();
        grid.Children.Add(new Viewbox { Child = _text, Stretch = Stretch.Uniform, Margin = new Thickness(isMore ? 9 : 10) });
        grid.Children.Add(_person);
        Child = grid;
        if (isMore)
        {
            // +N 数量气泡保留半透明底色保证文字可读
            Background = MoreBackground;
        }

        PointerEntered += (_, _) =>
        {
            _hover = true;
            ApplyVisual(_center);
        };
        PointerExited += (_, _) =>
        {
            _hover = false;
            ApplyVisual(_center);
        };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _pressed = false;
    }

    public string Key { get; }
    public bool IsMore { get; }
    public bool IsNew { get; set; } = true;
    public bool Leaving { get; private set; }
    public bool HoverEnabled { get; set; }
    public bool Animating { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Size { get; private set; }
    public double Alpha { get; private set; }

    public event Action<LanPeerAvatar>? Clicked;
    public event Action<PointerPressedEventArgs>? DragRequested;

    /// <summary>就地更新资料（名称/图标/状态），不重建控件。</summary>
    public void Update(LanPeerInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Icon))
        {
            _text.IsVisible = false;
            _person.IsVisible = _person.Data is not null;
        }
        else
        {
            _text.Text = info.Icon;
            _text.IsVisible = true;
            _person.IsVisible = false;
        }
        _tip = $"{info.Icon ?? "👤"} {info.DisplayName}\n{info.Status}";
        ApplyTip();
    }

    public void SetMoreCount(int count)
    {
        _text.Text = $"+{count}";
        _text.IsVisible = true;
        _tip = I18n.T("peer.bubble.more", count);
        ApplyTip();
    }

    public void SetTipEnabled(bool enabled)
    {
        _tipEnabled = enabled;
        ApplyTip();
    }

    private void ApplyTip() => ToolTip.SetTip(this, _tipEnabled ? _tip : null);

    public void SnapTo(double x, double y, double size, double alpha)
    {
        X = _toX = x;
        Y = _toY = y;
        Size = _toSize = size;
        Alpha = _toAlpha = alpha;
        Animating = false;
    }

    /// <summary>从当前值平滑过渡到目标（可中断：中途重设目标不会跳回起点）。</summary>
    public void AnimateTo(double x, double y, double size, double alpha, int delayMs, int durationMs)
    {
        if (Animating && Near(_toX, x) && Near(_toY, y) && Near(_toSize, size) && Near(_toAlpha, alpha))
        {
            return;
        }
        if (Near(X, x) && Near(Y, y) && Near(Size, size) && Near(Alpha, alpha))
        {
            SnapTo(x, y, size, alpha);
            return;
        }
        _fromX = X;
        _fromY = Y;
        _fromSize = Size;
        _fromAlpha = Alpha;
        _toX = x;
        _toY = y;
        _toSize = size;
        _toAlpha = alpha;
        _animStart = DateTime.UtcNow.AddMilliseconds(Math.Max(0, delayMs));
        _animDuration = Math.Max(1, durationMs);
        Animating = true;
    }

    public void BeginLeave()
    {
        Leaving = true;
        HoverEnabled = false;
        SetTipEnabled(false);
        AnimateTo(X, Y, Size * 0.6, 0, 0, LanPeerBubbleOptions.FadeDurationMs);
    }

    /// <summary>推进一帧插值（EaseOutCubic）；返回是否仍在动画中。</summary>
    public bool Step(DateTime now)
    {
        if (!Animating)
        {
            return false;
        }
        var t = (now - _animStart).TotalMilliseconds / _animDuration;
        if (t <= 0)
        {
            return true;
        }
        if (t >= 1)
        {
            SnapTo(_toX, _toY, _toSize, _toAlpha);
            return false;
        }
        var k = 1 - Math.Pow(1 - t, 3);
        X = _fromX + (_toX - _fromX) * k;
        Y = _fromY + (_toY - _fromY) * k;
        Size = _fromSize + (_toSize - _fromSize) * k;
        Alpha = _fromAlpha + (_toAlpha - _fromAlpha) * k;
        return true;
    }

    /// <summary>按当前插值状态更新画布位置、缩放、透明度与悬停外观。</summary>
    public void ApplyVisual(Point center)
    {
        _center = center;
        Canvas.SetLeft(this, center.X + X - BaseSize / 2);
        Canvas.SetTop(this, center.Y + Y - BaseSize / 2);
        var hover = _hover && HoverEnabled;
        var s = Size / BaseSize * (hover ? LanPeerBubbleOptions.HoverScale : 1);
        _scale.ScaleX = s;
        _scale.ScaleY = s;
        Opacity = Alpha;
        IsHitTestVisible = !Leaving && Alpha > 0.05;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _press = e;
        _pressPoint = e.GetPosition(null);
        _pressed = true;
        e.Handled = true;   // 阻止窗口级按下处理（立即拖动/双击），由下方移动阈值区分点击与拖动
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }
        var p = e.GetPosition(null);
        var dx = p.X - _pressPoint.X;
        var dy = p.Y - _pressPoint.Y;
        if (dx * dx + dy * dy < LanPeerBubbleOptions.DragThreshold * LanPeerBubbleOptions.DragThreshold)
        {
            return;
        }
        _pressed = false;
        var press = _press;
        _press = null;
        if (press is not null)
        {
            DragRequested?.Invoke(press);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }
        _pressed = false;
        _press = null;
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= Bounds.Width && p.Y <= Bounds.Height)
        {
            Clicked?.Invoke(this);
        }
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in s)
            {
                h = (h ^ c) * 16777619u;
            }
            return (int)(h & 0x7fffffff);
        }
    }
}

internal enum LanPeerBubbleState
{
    Hidden,
    Collapsed,
    Expanded,
    Transitioning,
}

/// <summary>
/// 伙伴气泡控制器：统一管理 Hidden/Collapsed/Expanded/Transitioning 状态、展开收拢动画（单一定时器、可中断）、
/// 离开延迟收拢、在线列表增删改动画与资源释放。头像本身不自行决定展开/收拢。
/// </summary>
internal sealed class LanPeerBubbleController : IDisposable
{
    private const string MoreKey = "\u0001more";

    private readonly Canvas _layer;
    private readonly Ellipse _hoverPad;
    private readonly Canvas _hoverLayer;
    private readonly Func<Point> _center;
    private readonly Func<Rect> _expandBounds;
    private readonly Func<bool> _pointerInside;
    private readonly LanPeerSource _source;
    private readonly Dictionary<string, LanPeerAvatar> _items = new(StringComparer.Ordinal);
    private readonly List<LanPeerAvatar> _leaving = new();
    private readonly DispatcherTimer _animTimer;
    private readonly DispatcherTimer _collapseTimer;
    private LanPeerAvatar? _more;
    private bool _expanded;
    private bool _disposed;
    private double _expandedPadRadius;

    /// <param name="layer">头像画布（位于气泡本体之上）。</param>
    /// <param name="hoverLayer">展开时的透明命中垫画布（位于气泡本体之下，填补头像与主球之间的空隙）。</param>
    /// <param name="center">气泡圆心在画布中的坐标（不含贴边平移，平移由画布变换承担）。</param>
    /// <param name="expandBounds">展开可用矩形（相对圆心，窗口 ∩ 屏幕工作区）。</param>
    /// <param name="pointerInside">指针是否仍在主球或伙伴区域内。</param>
    public LanPeerBubbleController(Canvas layer, Canvas hoverLayer, Func<Point> center, Func<Rect> expandBounds,
        Func<bool> pointerInside, A2AExtensionController a2a)
    {
        _layer = layer;
        _hoverLayer = hoverLayer;
        _center = center;
        _expandBounds = expandBounds;
        _pointerInside = pointerInside;
        _hoverPad = new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),   // 近透明但可命中（macOS 全透明像素会穿透）
            IsVisible = false,
        };
        _hoverLayer.Children.Add(_hoverPad);
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += OnAnimTick;
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LanPeerBubbleOptions.CollapseDelayMs) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_pointerInside())
            {
                SetExpanded(false);
            }
        };
        _source = new LanPeerSource(a2a);
        _source.PeersChanged += OnPeersChanged;
    }

    /// <summary>窗口需要更新紧凑模式/命中区域（展开开始、动画结束、列表变化）。</summary>
    public event Action? FootprintChanged;
    public event Action<string>? PeerClicked;
    public event Action? MoreClicked;
    public event Action<PointerPressedEventArgs>? DragRequested;

    public LanPeerBubbleState State =>
        _source.Current.Count == 0 && _leaving.Count == 0 ? LanPeerBubbleState.Hidden
        : _animTimer.IsEnabled ? LanPeerBubbleState.Transitioning
        : _expanded ? LanPeerBubbleState.Expanded
        : LanPeerBubbleState.Collapsed;

    /// <summary>展开或动画进行中：窗口需保持完整尺寸（macOS 不进入紧凑模式）。</summary>
    public bool NeedsFullWindow => _expanded || _animTimer.IsEnabled;

    /// <summary>当前是否有在线伙伴（窗口据此下移提示卡，避免与收拢头像重叠）。</summary>
    public bool HasPeers => _source.Current.Count > 0;

    /// <summary>展开区域命中半径（相对圆心，0 表示无需额外命中区）。收拢动画期间保持，避免被窗口区域裁剪。</summary>
    public double HitRadius => NeedsFullWindow && _expandedPadRadius > 0 ? _expandedPadRadius : 0;

    public void OnPointerEntered()
    {
        _collapseTimer.Stop();
        if (_source.Current.Count > 0)
        {
            SetExpanded(true);
        }
    }

    public void OnPointerExited()
    {
        if (!_expanded)
        {
            return;
        }
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    /// <summary>圆心位置变化（紧凑模式切换）后同步画布坐标。</summary>
    public void Reposition()
    {
        var c = _center();
        foreach (var item in AllItems())
        {
            item.ApplyVisual(c);
        }
        PlaceHoverPad(c);
    }

    /// <summary>窗口位置变化（拖动/贴边）后按新边界重算展开布局。</summary>
    public void Relayout()
    {
        if (_expanded)
        {
            Retarget(stagger: false);
            FootprintChanged?.Invoke();
        }
    }

    private void SetExpanded(bool expand)
    {
        if (_disposed || _expanded == expand || (expand && _source.Current.Count == 0))
        {
            return;
        }
        _expanded = expand;
        if (expand)
        {
            FootprintChanged?.Invoke();   // 先让窗口退出紧凑模式，再按最终窗口计算可用边界
            Retarget(stagger: true);
            FootprintChanged?.Invoke();   // 命中区并入展开范围
        }
        else
        {
            Retarget(stagger: true);      // 收拢动画结束后再收缩窗口与命中区
        }
    }

    private void OnPeersChanged()
    {
        if (_disposed)
        {
            return;
        }
        if (!_expanded && _source.Current.Count > 0 && _pointerInside())
        {
            SetExpanded(true);   // 指针停留在气泡上时有伙伴上线：直接展开
            return;
        }
        Retarget(stagger: false);
        FootprintChanged?.Invoke();
    }

    private void Retarget(bool stagger)
    {
        var peers = _source.Current;
        var n = peers.Count;
        var c = _center();

        foreach (var key in _items.Keys.Where(k => peers.All(p => p.NodeId != k)).ToList())
        {
            var gone = _items[key];
            _items.Remove(key);
            Leave(gone);
        }

        if (n == 0)
        {
            if (_more is not null)
            {
                Leave(_more);
                _more = null;
            }
            _expanded = false;
            _hoverPad.IsVisible = false;
            StartAnimation();
            return;
        }

        var list = new List<LanPeerAvatar>(n);
        foreach (var peer in peers)
        {
            if (!_items.TryGetValue(peer.NodeId, out var item))
            {
                item = CreateAvatar(peer.NodeId, isMore: false);
                _items[peer.NodeId] = item;
            }
            item.Update(peer);
            list.Add(item);
        }

        List<Point> slots = [];
        var size = LanPeerBubbleOptions.CollapsedSize;
        if (_expanded)
        {
            slots = LanPeerLayout.Expanded(n, _expandBounds()).Slots;
            size = LanPeerBubbleOptions.ExpandedSize;
        }
        int shown;
        if (slots.Count == 0)
        {
            var visible = Math.Min(n, LanPeerBubbleOptions.CollapsedMaxVisible + 1);
            slots = LanPeerLayout.Collapsed(visible);
            shown = n <= visible ? n : LanPeerBubbleOptions.CollapsedMaxVisible;
            if (_expanded)
            {
                size = LanPeerBubbleOptions.CollapsedSize;   // 极端情况下展开空间不足：回退到收拢形态
            }
        }
        else
        {
            shown = slots.Count >= n ? n : slots.Count - 1;
        }
        var overflow = n - shown;
        var usedSlots = shown + (overflow > 0 ? 1 : 0);
        var morePos = overflow > 0 ? slots[shown] : default;
        var duration = _expanded ? LanPeerBubbleOptions.ExpandDurationMs : LanPeerBubbleOptions.CollapseDurationMs;

        int Delay(int i) => stagger ? (_expanded ? i : Math.Max(0, usedSlots - 1 - i)) * LanPeerBubbleOptions.StaggerMs : 0;
        int Z(int i) => _expanded ? 100 : 100 - (int)Math.Round(Math.Abs(i - (usedSlots - 1) / 2.0) * 2);

        for (var i = 0; i < n; i++)
        {
            var item = list[i];
            var visibleItem = i < shown;
            var pos = visibleItem ? slots[i] : morePos;
            var targetSize = visibleItem ? size : size * 0.6;
            var targetAlpha = visibleItem ? 1.0 : 0.0;
            if (item.IsNew)
            {
                item.SnapTo(pos.X, pos.Y, size * 0.6, 0);   // 新上线：原位淡入 + 轻微放大
                item.IsNew = false;
            }
            item.AnimateTo(pos.X, pos.Y, targetSize, targetAlpha, visibleItem ? Delay(i) : 0,
                item.Alpha == 0 && targetAlpha > 0 ? LanPeerBubbleOptions.FadeDurationMs : duration);
            item.ZIndex = visibleItem ? Z(i) : 0;
            item.HoverEnabled = _expanded;
            item.SetTipEnabled(_expanded && visibleItem);
        }

        if (overflow > 0)
        {
            if (_more is null)
            {
                _more = CreateAvatar(MoreKey, isMore: true);
                _more.SnapTo(morePos.X, morePos.Y, size * 0.6, 0);
                _more.IsNew = false;
            }
            _more.SetMoreCount(overflow);
            _more.AnimateTo(morePos.X, morePos.Y, size, 1, Delay(shown), duration);
            _more.ZIndex = Z(shown);
            _more.HoverEnabled = _expanded;
            _more.SetTipEnabled(true);
        }
        else if (_more is not null)
        {
            Leave(_more);
            _more = null;
        }

        if (_expanded)
        {
            var used = slots.Take(usedSlots).ToList();
            var maxDist = used.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
            _expandedPadRadius = Math.Max(32, maxDist + size / 2 + 6);
            var maxBottom = Math.Max(0, used.Max(p => p.Y) + size / 2);
            _layer.MinHeight = Math.Max(0, c.Y + maxBottom + 4);   // 向下展开时撑高窗口（SizeToContent）
            _hoverPad.IsVisible = true;
            PlaceHoverPad(c);
        }
        else
        {
            _hoverPad.IsVisible = false;
        }
        StartAnimation();
    }

    private void PlaceHoverPad(Point c)
    {
        var r = _expandedPadRadius;
        _hoverPad.Width = r * 2;
        _hoverPad.Height = r * 2;
        Canvas.SetLeft(_hoverPad, c.X - r);
        Canvas.SetTop(_hoverPad, c.Y - r);
    }

    private LanPeerAvatar CreateAvatar(string key, bool isMore)
    {
        var item = new LanPeerAvatar(key, isMore);
        item.Clicked += OnAvatarClicked;
        item.DragRequested += OnAvatarDragRequested;
        _layer.Children.Add(item);
        return item;
    }

    private void OnAvatarClicked(LanPeerAvatar item)
    {
        if (item.IsMore)
        {
            MoreClicked?.Invoke();
        }
        else
        {
            PeerClicked?.Invoke(item.Key);
        }
    }

    private void OnAvatarDragRequested(PointerPressedEventArgs e) => DragRequested?.Invoke(e);

    private void Leave(LanPeerAvatar item)
    {
        item.BeginLeave();
        _leaving.Add(item);
    }

    private void StartAnimation()
    {
        if (!_animTimer.IsEnabled)
        {
            _animTimer.Start();
        }
        OnAnimTick(null, EventArgs.Empty);
    }

    private IEnumerable<LanPeerAvatar> AllItems()
    {
        foreach (var item in _items.Values)
        {
            yield return item;
        }
        if (_more is not null)
        {
            yield return _more;
        }
        foreach (var item in _leaving)
        {
            yield return item;
        }
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            _animTimer.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        var c = _center();
        var any = false;
        foreach (var item in AllItems())
        {
            any |= item.Step(now);
            item.ApplyVisual(c);
        }
        for (var i = _leaving.Count - 1; i >= 0; i--)
        {
            if (!_leaving[i].Animating)
            {
                var item = _leaving[i];
                item.Clicked -= OnAvatarClicked;
                item.DragRequested -= OnAvatarDragRequested;
                _layer.Children.Remove(item);
                _leaving.RemoveAt(i);
            }
        }
        if (any)
        {
            return;
        }
        if (!_animTimer.IsEnabled)
        {
            return;
        }
        _animTimer.Stop();   // 动画结束即停，空闲时无任何定时器在跑
        if (!_expanded)
        {
            _layer.MinHeight = 0;
        }
        FootprintChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _animTimer.Stop();
        _collapseTimer.Stop();
        _source.PeersChanged -= OnPeersChanged;
        _source.Dispose();
        foreach (var item in AllItems().ToList())
        {
            item.Clicked -= OnAvatarClicked;
            item.DragRequested -= OnAvatarDragRequested;
        }
        _items.Clear();
        _leaving.Clear();
        _more = null;
        _layer.Children.Clear();
        _hoverLayer.Children.Remove(_hoverPad);
    }
}
