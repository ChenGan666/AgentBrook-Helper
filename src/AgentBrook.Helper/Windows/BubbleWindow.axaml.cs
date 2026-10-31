using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using AgentBrook.Agent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// Siri 式悬浮气泡（应用默认形态）：
/// · 按住拖动 → 移动气泡位置（系统级原生拖拽，与光标严格 1:1）
/// · 双击 → 打开对话窗体
/// · 右键 → 菜单（打开对话 / 切换模型 / 退出）
///
/// 功能区拆分（同为 partial BubbleWindow，私有成员跨文件共享）：
///   BubbleWindow.Orbit.cs       工具/子Agent 轨道图标
///   BubbleWindow.Toast.cs       提示卡（问候/审批/提问）
///   BubbleWindow.Positioning.cs 布局常量、位置持久化、macOS 紧凑模式、圆心换算
///   BubbleWindow.Dock.cs        磁吸贴边半隐 / 悬停滑出
///   BubbleWindow.Visual.cs      状态→外观、齿轮自转、路径旋转
///   BubbleWindow.Peers.cs       伙伴气泡桥接（圆心/展开边界/交互区）
///   BubbleWindow.HitRegion.cs   Windows 透明区域点击穿透（P/Invoke）
/// </summary>
public partial class BubbleWindow : Window
{
    private readonly AssistantController _controller;
    private readonly Ellipse _glowOuter;
    private readonly Ellipse _body;
    private readonly Ellipse _workOverlay;
    // 中心图标用 Path（形状）：PathIcon 在 Avalonia 12 下无视旋转中心（实测绕 0,0 转），齿轮自转会偏轴
    private readonly Avalonia.Controls.Shapes.Path _bubbleIcon;
    private readonly Canvas _orbitLayer;
    private readonly StackPanel _toastPanel;
    private DateTime _lastPressTime = DateTime.MinValue;

    /// <summary>伙伴气泡：局域网在线已配对 Helper 头像（状态/布局/动画见 LanPeerBubbles.cs）。</summary>
    private readonly LanPeerBubbleController _peerBubbles;
    private readonly Canvas _peerLayer;
    private readonly Canvas _peerHoverLayer;

    public BubbleWindow()
    {
        InitializeComponent();
        _controller = App.Controller;

        // 统一用 FindControl 获取控件（不依赖 x:Name 生成字段）
        _glowOuter = this.FindControl<Ellipse>("GlowOuter")!;
        _body = this.FindControl<Ellipse>("Body")!;
        _workOverlay = this.FindControl<Ellipse>("WorkOverlay")!;
        _bubbleIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("BubbleIcon")!;
        _orbitLayer = this.FindControl<Canvas>("OrbitLayer")!;
        _toastPanel = this.FindControl<StackPanel>("ToastPanel")!;
        var root = this.FindControl<Panel>("Root")!;
        _peerLayer = this.FindControl<Canvas>("PeerLayer")!;
        _peerHoverLayer = this.FindControl<Canvas>("PeerHoverLayer")!;
        _peerLayer.RenderTransform = _peerShift;
        _peerHoverLayer.RenderTransform = _peerHoverShift;

        // 磁吸贴边：把 XAML 的纯缩放变换升级为「缩放+平移」组合，平移量由贴边逻辑驱动
        // （Root 与 OrbitLayer 必须同步平移，轨道图标才能跟着气泡一起半隐/滑出；
        //   Root 缩放 0.72=本体缩小20%，轨道图标维持 0.9）
        root.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(0.72, 0.72), _rootShift },
        };
        _orbitLayer.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(0.9, 0.9), _orbitShift },
        };

        // 不抢占焦点：气泡被点击时不会把其他应用切到后台
        ShowActivated = false;

        // 拖入文件到气泡 → 自动打开对话窗并加入附件
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);

        _controller.StateChanged += OnStateChanged;
        _controller.RemoteStateChanged += (state, detail) => Dispatcher.UIThread.Post(() => SetState(state, detail));
        _controller.RemoteEventRaised += OnControllerEvent;   // 远程工具调用同样进轨道图标
        _controller.ReadyChanged += () => Dispatcher.UIThread.Post(
            () => SetState(BrookRunState.Idle, null));
        _controller.EventRaised += OnControllerEvent;   // 工具/子Agent 轨道图标的数据源

        Opened += (_, _) =>
        {
            PlaceBubble();
            ApplyIdentityUi();
            Dispatcher.UIThread.Post(() =>
            {
                UpdateWindowLayoutMode();
                UpdateClickThroughRegion();
            }, DispatcherPriority.Background);
        };   // Opened 在 Show 后确定触发

        ScalingChanged += (_, _) =>
            Dispatcher.UIThread.Post(UpdateClickThroughRegion, DispatcherPriority.Background);

        root.PointerEntered += (_, _) =>
        {
            _collapseTimer?.Stop();   // 再次进入：取消待执行的收回
            SetDockExpanded(true);    // 磁吸贴边：鼠标滑过滑入屏内全部可见
        };
        root.PointerExited += (_, _) => ScheduleDockCollapse();

        // 伙伴气泡：主球与头像区域视为同一交互区，在两者间移动不会误收拢
        _peerBubbles = new LanPeerBubbleController(_peerLayer, _peerHoverLayer, PeerCenter, PeerExpandBounds,
            IsPointerInPeerZone, App.A2A.Controller);
        _peerBubbles.FootprintChanged += () =>
        {
            UpdateToastOffset();
            EnsureAutoHeight();
            UpdateWindowLayoutMode();
            UpdateClickThroughRegion();
        };
        _peerBubbles.PeerClicked += nodeId => App.ShowA2AWindow(nodeId);
        _peerBubbles.MoreClicked += () => App.ShowA2AWindow();
        _peerBubbles.DragRequested += e =>
        {
            try
            {
                BeginMoveDrag(e);
            }
            catch
            {
                // 拖动不可用时忽略，不影响头像交互
            }
        };
        root.PointerEntered += (_, _) => _peerBubbles.OnPointerEntered();
        root.PointerExited += (_, _) => _peerBubbles.OnPointerExited();
        foreach (var layer in new[] { _peerLayer, _peerHoverLayer })
        {
            layer.PointerEntered += (_, _) =>
            {
                _collapseTimer?.Stop();
                _peerBubbles.OnPointerEntered();
            };
            layer.PointerExited += (_, _) =>
            {
                _peerBubbles.OnPointerExited();
                ScheduleDockCollapse();
            };
        }
        Closed += (_, _) => _peerBubbles.Dispose();

        // 提示卡加入后 Bounds 才在布局完成时生效；逐次布局监听，尺寸变化即刷新命中区
        // （否则 Windows 上 SetWindowRgn 会把新卡片裁成只剩一条边框）
        _toastPanel.LayoutUpdated += (_, _) =>
        {
            if (_toastPanel.Bounds != _lastToastBounds)
            {
                _lastToastBounds = _toastPanel.Bounds;
                UpdateClickThroughRegion();
            }
        };

        root.ContextRequested += (_, e) =>
        {
            var openItem = new MenuItem { Header = I18n.T("ui.open.chat") };
            var modelItem = new MenuItem { Header = I18n.T("ui.switch.model") };
            var settingsItem = new MenuItem { Header = I18n.T("ui.open.settings") };
            var exitItem = new MenuItem { Header = I18n.T("ui.quit.value", AssistantIdentity.Current) };

            openItem.Click += (_, _) => _controller.ShowConversation();
            modelItem.Click += (_, _) => _controller.CycleModel();
            settingsItem.Click += (_, _) =>
            {
                SettingsWindow.ShowSingle();
            };
            exitItem.Click += (_, _) => _controller.Shutdown();

            var menu = new ContextMenu
            {
                Items = { openItem, modelItem, settingsItem, new Separator(), exitItem },
            };
            menu.Open(this);
            e.Handled = true;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ───────────────────────── 按住拖动 / 双击开窗 ─────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        StopDockSlide();   // 用户抓取气泡：终止滑动动画
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
        ApplyEdgeMagnet();   // 磁吸：靠近左右上边缘自动贴边半隐；越界（含下方）弹回可视区
        EnsureAutoHeight();        // 拖动结束：拖动期间若高度被固定则恢复自适应
        _peerBubbles.Relayout();   // 位置变化后按新屏幕边界重算展开布局
        SavePosition();      // 拖动/点击结束：记录贴边后的位置
    }

    private void OnMenuOpenChat(object? sender, RoutedEventArgs e) => _controller.ShowConversation();
    private void OnMenuCycleModel(object? sender, RoutedEventArgs e) => _controller.CycleModel();
    private void OnMenuExit(object? sender, RoutedEventArgs e) => _controller.Shutdown();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            return;
        }
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return;
        }
        var paths = files.OfType<IStorageFile>().Select(f => f.Path.LocalPath).ToList();
        if (paths.Count == 0)
        {
            return;
        }
        _controller.ShowConversation();
        _controller.ConversationWindow?.AddAttachmentFromDrop(paths);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 气泡窗口不允许被关闭（退出走右键菜单）；系统退出请求（关机/Cmd+Q，App 已判定空闲）放行
        SavePosition();   // 退出前记录位置
        if (App.IsQuitting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        base.OnClosing(e);
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
