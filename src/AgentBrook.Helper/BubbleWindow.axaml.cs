using AgentBrook.Agent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;
using System.Text;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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
    // 中心图标用 Path（形状）：PathIcon 在 Avalonia 12 下无视旋转中心（实测绕 0,0 转），齿轮自转会偏轴
    private readonly Avalonia.Controls.Shapes.Path _bubbleIcon;
    private readonly Canvas _orbitLayer;
    private readonly StackPanel _toastPanel;
    private DateTime _lastPressTime = DateTime.MinValue;

    /// <summary>轨道图标：工具/子Agent 执行期间绕气泡外圈显示，按启动顺序排布。</summary>
    private sealed record OrbitItem(string Tool, Border Chip);
    private readonly List<OrbitItem> _orbit = new();
    private const double OrbitRadius = 52;
    private const double OrbitChipSize = 20;
    private const double OrbitPeriodSeconds = 12;   // 绕圆一圈的时长
    private double _orbitPhase;                     // 当前环相位（弧度，0 = 第一个图标在正上方）
    private DispatcherTimer? _orbitTimer;

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

        // 不抢占焦点：气泡被点击时不会把其他应用切到后台
        ShowActivated = false;

        _controller.StateChanged += OnStateChanged;
        _controller.ReadyChanged += () => Dispatcher.UIThread.Post(
            () => SetState(BrookRunState.Idle, null));
        _controller.EventRaised += OnControllerEvent;   // 工具/子Agent 轨道图标的数据源

        Opened += (_, _) =>
        {
            PlaceBubble();
            ApplyIdentityUi();
        };   // Opened 在 Show 后确定触发

        root.ContextRequested += (_, e) =>
        {
            var openItem = new MenuItem { Header = I18n.T("打开对话") };
            var modelItem = new MenuItem { Header = I18n.T("切换模型") };
            var settingsItem = new MenuItem { Header = I18n.T("打开设置") };
            var exitItem = new MenuItem { Header = I18n.T("退出 {0}", AssistantIdentity.Current) };

            openItem.Click += (_, _) => _controller.ShowConversation();
            modelItem.Click += (_, _) => _controller.CycleModel();
            settingsItem.Click += (_, _) =>
            {
                var settings = new ModelSettingsWindow();
                settings.Show();
                settings.Activate();
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

    // ───────────────────────── F3：工具/子Agent 轨道图标 ─────────────────────────
    private void OnControllerEvent(BrookEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (e)
            {
                case BrookEvent.ToolStarted t when t.Name != "ask_user"
                    && !t.Name.StartsWith("log:") && t.Name != "__clear__":
                    AddOrbit(t.Name);
                    break;
                case BrookEvent.ToolCompleted tc:
                    RemoveOrbit(tc.Name);
                    break;
                case BrookEvent.TurnCompleted or BrookEvent.Failed:
                    ClearOrbits();   // 回合兜底清理（异常路径也有 ToolCompleted 配对，这里是保险）
                    break;
            }
        });
    }

    private void AddOrbit(string tool)
    {
        if (_orbit.Any(o => o.Tool == tool))
        {
            return;   // 同名工具重复发起（理论上顺序执行不会）不叠加
        }
        var (iconKey, color) = OrbitStyle(tool);
        var chip = new Border
        {
            Width = OrbitChipSize,
            Height = OrbitChipSize,
            CornerRadius = new CornerRadius(OrbitChipSize / 2),
            Background = new SolidColorBrush(Color.Parse(color)),
            BorderBrush = new SolidColorBrush(Color.Parse("#ffffff")),
            BorderThickness = new Thickness(1.2),
            Child = new PathIcon
            {
                Width = 12,
                Height = 12,
                Foreground = Brushes.White,
            },
            IsHitTestVisible = false,
        };
        if (this.TryFindResource(iconKey, out var res) && res is StreamGeometry geo)
        {
            ((PathIcon)chip.Child!).Data = geo;
        }
        _orbit.Add(new OrbitItem(tool, chip));
        _orbitLayer.Children.Add(chip);
        LayoutOrbit();
        StartOrbitTimer();
    }

    private void RemoveOrbit(string tool)
    {
        var item = _orbit.FirstOrDefault(o => o.Tool == tool);
        if (item is null)
        {
            return;
        }
        // 先退出编队：剩余图标立即重排、定时器可停；被移除的芯片原地渐隐后从画布删除
        _orbit.Remove(item);
        LayoutOrbit();
        StopOrbitTimerIfEmpty();
        _ = FadeOutAndRemove(item.Chip, _orbitLayer);
    }

    /// <summary>渐隐动画（320ms 透明度 1→0）结束后把芯片从轨道画布移除。
    /// 用 DoubleTransition + 置终态实现（KeyFrame/Setter 在 Avalonia 12 命名空间有变动，transition 方式最稳）。</summary>
    private static async Task FadeOutAndRemove(Border chip, Canvas orbitLayer)
    {
        try
        {
            chip.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(320),
                    Easing = new SineEaseOut(),
                },
            };
            chip.Opacity = 0;
            await Task.Delay(340);   // 略长于动画时长，保证渐隐播完
        }
        catch
        {
            // 窗口关闭等场景可能中断，走 finally 兜底移除
        }
        finally
        {
            orbitLayer.Children.Remove(chip);
        }
    }

    private void ClearOrbits()
    {
        if (_orbit.Count == 0)
        {
            return;
        }
        var chips = _orbit.Select(o => o.Chip).ToList();
        _orbit.Clear();
        StopOrbitTimerIfEmpty();
        foreach (var chip in chips)
        {
            _ = FadeOutAndRemove(chip, _orbitLayer);   // 回合结束同样渐隐收场
        }
    }

    /// <summary>按启动顺序绕圆排布：相位 0 时第一个在正上方，后续顺时针均分。</summary>
    private void LayoutOrbit()
    {
        for (var i = 0; i < _orbit.Count; i++)
        {
            var angle = -Math.PI / 2 + _orbitPhase + i * (2 * Math.PI / _orbit.Count);
            var x = 48 + OrbitRadius * Math.Cos(angle) - OrbitChipSize / 2;
            var y = 48 + OrbitRadius * Math.Sin(angle) - OrbitChipSize / 2;
            Canvas.SetLeft(_orbit[i].Chip, x);
            Canvas.SetTop(_orbit[i].Chip, y);
        }
    }

    /// <summary>
    /// 绕圆动画：定时器逐帧按相位重算各图标的 DIP 坐标。
    /// 有意不用 RotateTransform——其与样式动画/Origin 的组合语义在 Avalonia 中不可靠
    /// （此前两次定位错误均源于此）；直接重算坐标在任何 DPI 下都精确。
    /// </summary>
    private void StartOrbitTimer()
    {
        if (_orbitTimer is null)
        {
            _orbitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };   // ~30fps
            _orbitTimer.Tick += (_, _) =>
            {
                _orbitPhase = (_orbitPhase + 2 * Math.PI * 0.033 / OrbitPeriodSeconds) % (2 * Math.PI);
                LayoutOrbit();
            };
        }
        if (!_orbitTimer.IsEnabled)
        {
            _orbitTimer.Start();
        }
    }

    private void StopOrbitTimerIfEmpty()
    {
        if (_orbit.Count == 0 && _orbitTimer is { IsEnabled: true })
        {
            _orbitTimer.Stop();
        }
    }

    /// <summary>工具名 →（图标资源键， chip 底色）：一眼可辨操作类型。</summary>
    private static (string IconKey, string Color) OrbitStyle(string tool) => tool switch
    {
        "run_command" => ("Icon.Terminal", "#ff9500"),
        "list_dir" or "read_file" or "write_file" or "append_file" => ("Icon.File", "#34c759"),
        "memory_read" or "memory_write" or "memory_append" => ("Icon.Memory", "#af52de"),
        "list_skills" or "load_skill" or "create_skill" or "install_skill" or "skill_market" => ("Icon.Book", "#5ac8fa"),
        "delegate_task" or "spawn_worker" or "assign_task" or "send_to_worker" or "broadcast" or "start_project" => ("Icon.Robot", "#ff2d55"),
        _ when tool.StartsWith("mcp_") => ("Icon.Bolt", "#8e8e93"),
        _ => ("Icon.Settings", "#4a9eff"),
    };

    // ───────────────────────── F1/F2：气泡提示卡（问候语 / 审批 / 提问） ─────────────────────────
    /// <summary>显示一张提示卡；openConversationOnClick=true 时点卡片打开对话窗。</summary>
    public void ShowToast(string text, string accent, TimeSpan? autoClose,
        IEnumerable<(string Label, Action OnClick)>? actions = null, bool openConversationOnClick = false)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#ee2a2a2a")),
            BorderBrush = new SolidColorBrush(Color.Parse(accent)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8),
            MaxWidth = 228,
        };
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
        });
        if (actions is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var (label, onClick) in actions)
            {
                var btn = new Button
                {
                    Content = label,
                    FontSize = 12,
                    Padding = new Thickness(10, 3),
                    Background = new SolidColorBrush(Color.Parse(accent)),
                    Foreground = Brushes.White,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                btn.Click += (_, _) =>
                {
                    onClick();
                    RemoveToast(card);
                };
                row.Children.Add(btn);
            }
            stack.Children.Add(row);
        }
        card.Child = stack;
        if (openConversationOnClick)
        {
            card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            var tap = false;   // 按钮点击也会冒泡到卡片，置位后忽略
            foreach (var child in stack.Children.OfType<StackPanel>())
            {
                child.PointerPressed += (_, _) => tap = true;
            }
            card.PointerReleased += (_, _) =>
            {
                if (!tap)
                {
                    _controller.ShowConversation();
                }
                RemoveToast(card);
            };
        }
        _toastPanel.Children.Add(card);
        while (_toastPanel.Children.Count > 3)
        {
            _toastPanel.Children.RemoveAt(0);   // 最多 3 张，丢最旧
        }
        if (autoClose is { } delay)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RemoveToast(card);
            };
            timer.Start();
        }
    }

    private void RemoveToast(Border card) => _toastPanel.Children.Remove(card);

    /// <summary>审批快捷卡：不拉起对话窗，直接在气泡上批准/拒绝（对话流中的卡片状态会同步）。</summary>
    public void ShowApprovalToast(string toolName, string? argsJson, TaskCompletionSource<ApprovalDecision> tcs)
    {
        var summary = _controller.Conversation.SummarizeApprovalPublic(toolName, argsJson);
        ShowToast(summary, "#ffb340", autoClose: null,
        [
            (I18n.T("批准"), () => tcs.TrySetResult(ApprovalDecision.Approve())),
            (I18n.T("拒绝"), () => tcs.TrySetResult(ApprovalDecision.Reject())),
        ]);
    }

    /// <summary>提问提醒卡：气泡上提示有问题待回答，点「去回答」打开对话窗。</summary>
    public void ShowAskToast(string question, TaskCompletionSource<string> tcs)
    {
        var preview = question.Length > 80 ? question[..80] + "…" : question;
        ShowToast(preview, "#ffb340", autoClose: null,
        [
            (I18n.T("去回答"), () => _controller.ShowConversation()),
        ]);
    }

    private void PlaceBubble()
    {
        // 窗口 240 宽、气泡（96x96）顶部居中且下移 24：气泡在窗口内的偏移
        const double bubbleOffsetX = (240 - 96) / 2.0;   // 72
        const double bubbleOffsetY = 24;
        var saved = UiPrefs.Load<BubblePos>("bubble-pos.json");
        if (saved is not null)
        {
            // 旧版窗口 96x96 时记录的是气泡左上角；换算成新窗口原点
            var sx = Math.Max(-1000, Math.Min(saved.X - bubbleOffsetX, 20000));
            var sy = Math.Max(-100, Math.Min(saved.Y - bubbleOffsetY, 20000));
            // 可见性保护：气泡圆心必须落在任一显示器工作区内，否则回落默认位置
            var bubbleCenter = new PixelPoint((int)(sx + bubbleOffsetX + 48), (int)(sy + bubbleOffsetY + 48));
            if (Screens.All.Any(s => s.WorkingArea.Contains(bubbleCenter)))
            {
                Position = new PixelPoint((int)sx, (int)sy);
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
        var x = area.X + area.Width - 96 - 24 - bubbleOffsetX;
        var y = area.Y + 108 - bubbleOffsetY;
        // 兜底 clamp：保证气泡圆完整落在主屏工作区内（下方提示区允许随屏幕裁剪）
        x = Math.Max(area.X + 8 - bubbleOffsetX, Math.Min(x, area.X + area.Width - 96 - 8 - bubbleOffsetX));
        y = Math.Max(area.Y + 8 - bubbleOffsetY, Math.Min(y, area.Y + area.Height - 96 - 8 - bubbleOffsetY));
        Position = new PixelPoint((int)x, (int)y);
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
                StartGearSpin();   // 处理中：中心齿轮慢速自转
                break;

            case BrookRunState.AwaitingApproval:
                SetLook(iconKey: "Icon.Shield", glow: "#ffb340", bodyTop: "#ffd27a", bodyBottom: "#ff9500", ring: false);
                StopGearSpin();
                break;

            case BrookRunState.AwaitingUserAnswer:
                SetLook(iconKey: "Icon.Question", glow: "#ffb340", bodyTop: "#ffd27a", bodyBottom: "#ff9500", ring: false);
                StopGearSpin();
                break;

            case BrookRunState.Idle:
            default:
                SetLook(iconKey: "Icon.Sparkle", glow: "#4a9eff", bodyTop: "#6bb9ff", bodyBottom: "#2f7bff", ring: false);
                StopGearSpin();
                break;
        }
    }

    // ───────────────────────── 处理中：中心齿轮慢速自转 ─────────────────────────
    private const int GearFrameCount = 72;        // 预生成帧数（每帧 5°）
    private const double GearFrameIntervalMs = 50; // 帧间隔：72 × 50ms ≈ 3.6 秒/圈，慢速平滑
    private const string GearPathData = "M12.0122 2.25C12.7462 2.25846 13.4773 2.34326 14.1937 2.50304C14.5064 2.57279 14.7403 2.83351 14.7758 3.15196L14.946 4.67881C15.0231 5.37986 15.615 5.91084 16.3206 5.91158C16.5103 5.91188 16.6979 5.87238 16.8732 5.79483L18.2738 5.17956C18.5651 5.05159 18.9055 5.12136 19.1229 5.35362C20.1351 6.43464 20.8889 7.73115 21.3277 9.14558C21.4223 9.45058 21.3134 9.78203 21.0564 9.9715L19.8149 10.8866C19.4607 11.1468 19.2516 11.56 19.2516 11.9995C19.2516 12.4389 19.4607 12.8521 19.8157 13.1129L21.0582 14.0283C21.3153 14.2177 21.4243 14.5492 21.3297 14.8543C20.8911 16.2685 20.1377 17.5649 19.1261 18.6461C18.9089 18.8783 18.5688 18.9483 18.2775 18.8206L16.8712 18.2045C16.4688 18.0284 16.0068 18.0542 15.6265 18.274C15.2463 18.4937 14.9933 18.8812 14.945 19.3177L14.7759 20.8444C14.741 21.1592 14.5122 21.4182 14.204 21.4915C12.7556 21.8361 11.2465 21.8361 9.79803 21.4915C9.48991 21.4182 9.26105 21.1592 9.22618 20.8444L9.05736 19.32C9.00777 18.8843 8.75434 18.498 8.37442 18.279C7.99451 18.06 7.5332 18.0343 7.1322 18.2094L5.72557 18.8256C5.43422 18.9533 5.09403 18.8833 4.87678 18.6509C3.86462 17.5685 3.11119 16.2705 2.6732 14.8548C2.57886 14.5499 2.68786 14.2186 2.94485 14.0293L4.18818 13.1133C4.54232 12.8531 4.75147 12.4399 4.75147 12.0005C4.75147 11.561 4.54232 11.1478 4.18771 10.8873L2.94516 9.97285C2.6878 9.78345 2.5787 9.45178 2.67337 9.14658C3.11212 7.73215 3.86594 6.43564 4.87813 5.35462C5.09559 5.12236 5.43594 5.05259 5.72724 5.18056L7.12762 5.79572C7.53056 5.97256 7.9938 5.94585 8.37577 5.72269C8.75609 5.50209 9.00929 5.11422 9.05817 4.67764L9.22824 3.15196C9.26376 2.83335 9.49786 2.57254 9.8108 2.50294C10.5281 2.34342 11.26 2.25865 12.0122 2.25ZM12.0124 3.7499C11.5583 3.75524 11.1056 3.79443 10.6578 3.86702L10.5489 4.84418C10.4471 5.75368 9.92003 6.56102 9.13042 7.01903C8.33597 7.48317 7.36736 7.53903 6.52458 7.16917L5.62629 6.77456C5.05436 7.46873 4.59914 8.25135 4.27852 9.09168L5.07632 9.67879C5.81513 10.2216 6.25147 11.0837 6.25147 12.0005C6.25147 12.9172 5.81513 13.7793 5.0771 14.3215L4.27805 14.9102C4.59839 15.752 5.05368 16.5361 5.626 17.2316L6.53113 16.8351C7.36923 16.4692 8.33124 16.5227 9.12353 16.9794C9.91581 17.4361 10.4443 18.2417 10.548 19.1526L10.657 20.1365C11.5466 20.2878 12.4555 20.2878 13.3451 20.1365L13.4541 19.1527C13.5549 18.2421 14.0828 17.4337 14.876 16.9753C15.6692 16.5168 16.6332 16.463 17.4728 16.8305L18.3772 17.2267C18.949 16.5323 19.4041 15.7495 19.7247 14.909L18.9267 14.3211C18.1879 13.7783 17.7516 12.9162 17.7516 11.9995C17.7516 11.0827 18.1879 10.2206 18.9258 9.67847L19.7227 9.09109C19.4021 8.25061 18.9468 7.46784 18.3748 6.77356L17.4783 7.16737C17.113 7.32901 16.7178 7.4122 16.3187 7.41158C14.849 7.41004 13.6155 6.30355 13.4551 4.84383L13.3462 3.8667C12.9007 3.7942 12.4526 3.75512 12.0124 3.7499ZM11.9997 8.24995C14.0708 8.24995 15.7497 9.92888 15.7497 12C15.7497 14.071 14.0708 15.75 11.9997 15.75C9.92863 15.75 8.2497 14.071 8.2497 12C8.2497 9.92888 9.92863 8.24995 11.9997 8.24995ZM11.9997 9.74995C10.7571 9.74995 9.7497 10.7573 9.7497 12C9.7497 13.2426 10.7571 14.25 11.9997 14.25C13.2423 14.25 14.2497 13.2426 14.2497 12C14.2497 10.7573 13.2423 9.74995 11.9997 9.74995Z";
    private StreamGeometry[]? _gearFrames;        // 预生成的旋转几何帧（每帧路径数据真正不同）
    private int _gearFrameIndex;
    private DispatcherTimer? _gearTimer;

    /// <summary>
    /// 齿轮自转：预生成 24 帧旋转后的路径数据（逐坐标点绕齿轮中心旋转后重新生成字符串），
    /// 定时器逐帧切换 Data。每帧是真正不同的路径数据，渲染器必然画出不同形状——
    /// 不依赖任何变换语义（RenderTransform 的旋转中心与 Geometry.Transform 的失效
    /// 在部分 Windows 环境下均不可靠），与轨道图标的逐帧坐标方案同级可靠。
    /// </summary>
    private void StartGearSpin()
    {
        if (_gearFrames is null)
        {
            try
            {
                _gearFrames = new StreamGeometry[GearFrameCount];
                for (var k = 0; k < GearFrameCount; k++)
                {
                    _gearFrames[k] = StreamGeometry.Parse(RotateGlyphPath(GearPathData, k * 360.0 / GearFrameCount));
                }
            }
            catch
            {
                _gearFrames = null;
                return;   // 帧生成失败：保持静态齿轮，不影响其他功能
            }
        }
        if (_gearTimer is null)
        {
            _gearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GearFrameIntervalMs) };
            _gearTimer.Tick += (_, _) =>
            {
                _gearFrameIndex = (_gearFrameIndex + 1) % GearFrameCount;
                if (_gearFrames is not null)
                {
                    _bubbleIcon.Data = _gearFrames[_gearFrameIndex];
                }
            };
        }
        _gearFrameIndex = 0;
        _bubbleIcon.Data = _gearFrames[0];
        if (!_gearTimer.IsEnabled)
        {
            _gearTimer.Start();
        }
    }

    private void StopGearSpin()
    {
        if (_gearTimer is { IsEnabled: true })
        {
            _gearTimer.Stop();
        }
        _gearFrameIndex = 0;
        // Data 由 SetLook 设置为当前状态对应图标（盾牌/问号/sparkle），此处无需处理
    }

    // ───────────────────────── 路径数据旋转（M/L/C/Z 绝对坐标） ─────────────────────────
    /// <summary>把 SVG 路径数据的每个坐标点绕 (12,12)（24x24 字形的中心）旋转 degrees 度后重排。</summary>
    private static string RotateGlyphPath(string data, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        string F(double v) => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);

        var tokens = TokenizePath(data);
        var sb = new StringBuilder();
        double curX = 0, curY = 0, startX = 0, startY = 0;
        char cmd = 'M';
        var firstOfCmd = true;
        var i = 0;
        while (i < tokens.Count)
        {
            if (tokens[i] is char ch)
            {
                cmd = ch;
                firstOfCmd = true;
                if (cmd == 'Z')
                {
                    sb.Append('Z');
                    curX = startX;
                    curY = startY;
                }
                i++;
                continue;
            }

            switch (cmd)
            {
                case 'M' or 'L':
                {
                    var x = (double)tokens[i];
                    var y = (double)tokens[i + 1];
                    double Rx() => 12 + (x - 12) * cos - (y - 12) * sin;
                    double Ry() => 12 + (x - 12) * sin + (y - 12) * cos;
                    sb.Append(cmd == 'M' && firstOfCmd ? 'M' : 'L')
                      .Append(F(Rx())).Append(' ').Append(F(Ry()));
                    curX = x;
                    curY = y;
                    if (cmd == 'M' && firstOfCmd)
                    {
                        startX = x;
                        startY = y;
                    }
                    i += 2;
                    break;
                }
                case 'C':
                {
                    for (var p = 0; p < 3; p++)
                    {
                        var x = (double)tokens[i + p * 2];
                        var y = (double)tokens[i + p * 2 + 1];
                        double Rx() => 12 + (x - 12) * cos - (y - 12) * sin;
                        double Ry() => 12 + (x - 12) * sin + (y - 12) * cos;
                        sb.Append(p == 0 ? 'C' : ' ').Append(F(Rx())).Append(' ').Append(F(Ry()));
                    }
                    i += 6;
                    break;
                }
                case 'H':
                {
                    var x = (double)tokens[i];
                    sb.Append('L').Append(F(12 + (x - 12) * cos - (curY - 12) * sin)).Append(' ')
                      .Append(F(12 + (x - 12) * sin + (curY - 12) * cos));
                    curX = x;
                    i += 1;
                    break;
                }
                case 'V':
                {
                    var y = (double)tokens[i];
                    sb.Append('L').Append(F(12 + (curX - 12) * cos - (y - 12) * sin)).Append(' ')
                      .Append(F(12 + (curX - 12) * sin + (y - 12) * cos));
                    curY = y;
                    i += 1;
                    break;
                }
                default:
                    return data;   // 未支持的命令（如 A 弧）：原样返回，退化为静态齿轮
            }
            firstOfCmd = false;
        }
        return sb.ToString();
    }

    /// <summary>把路径数据切分为命令字母与数字（Fluent 图标均为绝对坐标、无指数记数）。</summary>
    private static List<object> TokenizePath(string s)
    {
        var list = new List<object>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsAsciiLetter(c))
            {
                list.Add(c);
                i++;
            }
            else if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                var j = i + 1;
                while (j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] == '.'))
                {
                    j++;
                }
                list.Add(double.Parse(s[i..j], CultureInfo.InvariantCulture));
                i = j;
            }
            else
            {
                i++;   // 空白/逗号
            }
        }
        return list;
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
