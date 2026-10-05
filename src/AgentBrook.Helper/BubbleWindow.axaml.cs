using AgentBrook.Agent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;

namespace AgentBrook.Helper;

/// <summary>
/// Siri 式悬浮气泡（应用默认形态）：
/// · 按住拖动 → 移动气泡位置（系统级原生拖拽，与光标严格 1:1）
/// · 双击 → 打开对话窗体
/// · 右键 → 菜单（打开对话 / 切换模型 / 退出）
/// </summary>
public partial class BubbleWindow : Window
{
    private readonly AssistantController _controller;
    private readonly Ellipse _glowOuter;
    private readonly Ellipse _body;
    private readonly Ellipse _workOverlay;
    private readonly PathIcon _bubbleIcon;
    private DateTime _lastPressTime = DateTime.MinValue;

    public BubbleWindow()
    {
        InitializeComponent();
        _controller = App.Controller;

        // 统一用 FindControl 获取控件（不依赖 x:Name 生成字段）
        _glowOuter = this.FindControl<Ellipse>("GlowOuter")!;
        _body = this.FindControl<Ellipse>("Body")!;
        _workOverlay = this.FindControl<Ellipse>("WorkOverlay")!;
        _bubbleIcon = this.FindControl<PathIcon>("BubbleIcon")!;
        var root = this.FindControl<Panel>("Root")!;

        // 不抢占焦点：气泡被点击时不会把其他应用切到后台
        ShowActivated = false;

        _controller.StateChanged += OnStateChanged;
        _controller.ReadyChanged += () => Dispatcher.UIThread.Post(
            () => SetState(BrookRunState.Idle, null));

        Opened += (_, _) =>
        {
            PlaceBubble();
            ApplyIdentityUi();
        };   // Opened 在 Show 后确定触发

        root.ContextRequested += (_, e) =>
        {
            var openItem = new MenuItem { Header = I18n.T("打开对话窗体") };
            var modelItem = new MenuItem { Header = I18n.T("切换模型") };
            var fullItem = new MenuItem { Header = _controller.FullAccess ? I18n.T("关闭完全访问") : I18n.T("开启完全访问（审批自动通过）") };
            var exitItem = new MenuItem { Header = I18n.T("退出 {0}", AssistantIdentity.Current) };

            openItem.Click += (_, _) => _controller.ShowConversation();
            modelItem.Click += (_, _) => _controller.CycleModel();
            fullItem.Click += (_, _) =>
            {
                _controller.FullAccess = !_controller.FullAccess;
                fullItem.Header = _controller.FullAccess ? I18n.T("关闭完全访问") : I18n.T("开启完全访问（审批自动通过）");
                _controller.Conversation?.UpdateFullAccessVisual();   // 主窗芯片与 tooltip 同步
            };
            exitItem.Click += (_, _) => _controller.Shutdown();

            var menu = new ContextMenu
            {
                Items = { openItem, modelItem, fullItem, new Separator(), exitItem },
            };
            menu.Open(this);
            e.Handled = true;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void PlaceBubble()
    {
        // 恢复上次位置（用户拖动过的位置跨启动记忆）；首启固定主屏右上角。
        var saved = UiPrefs.Load<BubblePos>("bubble-pos.json");
        if (saved is not null)
        {
            var sx = Math.Max(0, Math.Min(saved.X, 20000));
            var sy = Math.Max(0, Math.Min(saved.Y, 20000));
            // 可见性保护：气泡中心必须落在任一显示器工作区内，否则回落默认位置
            var center = new PixelPoint(sx + (int)Width / 2, sy + (int)Height / 2);
            if (Screens.All.Any(s => s.WorkingArea.Contains(center)))
            {
                Position = new PixelPoint(sx, sy);
                return;
            }
        }

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }
        var area = screen.WorkingArea;
        var x = area.X + area.Width - (int)Width - 24;
        var y = area.Y + 108;
        // 兜底 clamp：保证窗口完整落在主屏工作区内
        x = Math.Max(area.X + 8, Math.Min(x, area.X + area.Width - (int)Width - 8));
        y = Math.Max(area.Y + 8, Math.Min(y, area.Y + area.Height - (int)Height - 8));
        Position = new PixelPoint(x, y);
    }

    /// <summary>把当前气泡位置写入偏好（拖动结束/退出时调用）。</summary>
    private void SavePosition()
    {
        UiPrefs.Save("bubble-pos.json", new BubblePos(Position.X, Position.Y));
    }

    // ───────────────────────── 状态 → 动画 ─────────────────────────
    private void OnStateChanged(BrookRunState state, string? detail)
    {
        Dispatcher.UIThread.Post(() => SetState(state, detail));
    }

    private void SetState(BrookRunState state, string? detail)
    {
        switch (state)
        {
            case BrookRunState.Thinking:
            case BrookRunState.CallingTool:
                SetLook(iconKey: "Icon.Settings", glow: "#4a9eff", bodyTop: "#6bb9ff", bodyBottom: "#2f7bff", ring: false);
                break;

            case BrookRunState.AwaitingApproval:
                SetLook(iconKey: "Icon.Shield", glow: "#ffb340", bodyTop: "#ffd27a", bodyBottom: "#ff9500", ring: false);
                break;

            case BrookRunState.AwaitingUserAnswer:
                SetLook(iconKey: "Icon.Question", glow: "#ffb340", bodyTop: "#ffd27a", bodyBottom: "#ff9500", ring: false);
                break;

            case BrookRunState.Idle:
            default:
                SetLook(iconKey: "Icon.Sparkle", glow: "#4a9eff", bodyTop: "#6bb9ff", bodyBottom: "#2f7bff", ring: false);
                break;
        }
    }

    private void SetLook(string iconKey, string glow, string bodyTop, string bodyBottom, bool ring)
    {
        if (this.TryFindResource(iconKey, out var res) && res is StreamGeometry geo)
        {
            _bubbleIcon.Data = geo;
        }
        _glowOuter.Fill = new SolidColorBrush(Color.Parse(glow));
        if (_body.Fill is LinearGradientBrush brush && brush.GradientStops.Count == 2)
        {
            brush.GradientStops[0].Color = Color.Parse(bodyTop);
            brush.GradientStops[1].Color = Color.Parse(bodyBottom);
        }
        _workOverlay.IsVisible = ring;
    }

    // ───────────────────────── 按住拖动 / 双击开窗 ─────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var now = DateTime.Now;
        var isDouble = e.ClickCount >= 2 || (now - _lastPressTime).TotalMilliseconds < 350;
        _lastPressTime = now;

        if (isDouble)
        {
            // 双击：打开对话窗体（不启动拖拽）
            _controller.ShowConversation();
            return;
        }

        // 系统级原生拖拽：窗口由 macOS 直接移动，与光标严格 1:1 同步
        BeginMoveDrag(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        SavePosition();   // 拖动/点击结束：记录当前位置
    }

    private void OnMenuOpenChat(object? sender, RoutedEventArgs e) => _controller.ShowConversation();
    private void OnMenuCycleModel(object? sender, RoutedEventArgs e) => _controller.CycleModel();
    private void OnMenuExit(object? sender, RoutedEventArgs e) => _controller.Shutdown();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 气泡窗口不允许被关闭（退出走右键菜单）
        SavePosition();   // 退出前记录位置
        e.Cancel = true;
        base.OnClosing(e);
    }

    /// <summary>删除位置偏好并回到默认位置（设置窗口的重置按钮调用）。</summary>
    public void ResetPositionToDefault()
    {
        UiPrefs.Delete("bubble-pos.json");
        PlaceBubble();
    }

    /// <summary>把个性化设置（名字/图标）应用到气泡。</summary>
    public void ApplyIdentityUi()
    {
        Title = AssistantIdentity.Current;   // 气泡标题=助手名
        if (this.TryFindResource(AssistantIdentity.BubbleIcon, out var geo) && geo is StreamGeometry g)
        {
            _bubbleIcon.Data = g;
        }
    }
}
