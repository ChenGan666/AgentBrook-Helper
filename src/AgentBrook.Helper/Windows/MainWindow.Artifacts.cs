using AgentBrook.Agent;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Helper.Support;
using Avalonia;
using Avalonia.Controls;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 右侧「任务产物」独立浮窗的宿主接线（纯新增功能，既有方法零改动）：
/// 顶栏切换按钮（ArtifactsBtn，激活态点亮）控制一个无边框独立窗口（ArtifactsPanelWindow），
/// 面板贴主窗口右缘外侧、同高跟随 —— 主窗口会话界面不挤压、布局零变化。
/// 数据源 = Controller.GetSessionArtifacts()（全量转写镜像按任务目录聚合，同路径文件取最新记录）。
/// 全部接线以追加订阅完成：ReadyChanged / SessionsChanged / SessionReloaded / EventRaised(ArtifactsReady) /
/// I18n.LanguageChanged / 本窗口 PropertyChanged+PositionChanged+ScalingChanged（跟随与显隐联动）。
/// 面板开合状态经 UiPrefs 持久化（chat-ui.json）。
/// </summary>
public partial class MainWindow
{
    private bool _artifactsPanelOpen;          // 用户期望的开合状态（默认收起，启动不恢复）
    private ArtifactsPanelWindow? _artifactsWindow;
    private Button? _artifactsBtn;

    /// <summary>ctor 调用：挂按钮与全部追加订阅。面板默认收起（不做开合持久化：每次启动一律收起）。</summary>
    private void InitArtifactsPanel()
    {
        _artifactsBtn = this.FindControl<Button>("ArtifactsBtn");
        if (_artifactsBtn is not null)
        {
            ToolTip.SetTip(_artifactsBtn, I18n.T("ui.artifacts.title"));
            _artifactsBtn.Click += (_, _) => SetArtifactsPanel(!_artifactsPanelOpen);
        }

        // 追加订阅（与既有订阅者并存，互不影响）
        _controller.ReadyChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshArtifactsPanel);
        _controller.SessionsChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshArtifactsPanel);
        _controller.SessionReloaded += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshArtifactsPanel);
        _controller.EventRaised += OnArtifactsPanelEvent;   // ArtifactsReady 时转写镜像已含本回合产物（SaveSessionCoreAsync 先于事件）
        I18n.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_artifactsBtn is not null)
            {
                ToolTip.SetTip(_artifactsBtn, I18n.T("ui.artifacts.title"));
            }
            RefreshArtifactsPanel();
        });

        // 主窗口显隐联动面板（关闭=隐藏、ShowFromBubble 恢复显示）：追加订阅，不触碰 OnClosing/ShowFromBubble
        this.PropertyChanged += OnArtifactsOwnerPropertyChanged;
        // 主窗口移动/缩放/跨屏缩放变化 → 面板贴边跟随
        this.PositionChanged += (_, _) => RepositionArtifactsWindow();
        this.ScalingChanged += (_, _) => RepositionArtifactsWindow();
        // z 序要求：面板恒在主窗口之下（MainWindow 压在面板上，层叠衬板效果）。
        // 普通窗口天然满足静止态；用户点击面板会把它前置，点回主窗口即恢复（拖动/激活主窗口同理）。
    }

    private void OnArtifactsPanelEvent(BrookEvent e)
    {
        if (e is not BrookEvent.ArtifactsReady) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshArtifactsPanel);
    }

    /// <summary>主窗口可见性/尺寸变化（追加订阅路径）：显隐联动面板，尺寸变化时贴边重定位。</summary>
    private void OnArtifactsOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsVisibleProperty)
        {
            SyncArtifactsWindow();   // 主窗口隐藏 → 面板藏；主窗口显示且状态开 → 定位并显示面板
        }
        else if (e.Property == Window.WidthProperty || e.Property == Window.HeightProperty)
        {
            RepositionArtifactsWindow();
        }
    }

    /// <summary>展开/收起面板：翻按钮点亮态、显隐独立窗口；展开时定位并立即刷新内容。</summary>
    private void SetArtifactsPanel(bool open)
    {
        _artifactsPanelOpen = open;
        if (_artifactsBtn is not null)
        {
            if (open)
            {
                _artifactsBtn.Classes.Add("active");
            }
            else
            {
                _artifactsBtn.Classes.Remove("active");
            }
        }
        if (open)
        {
            SyncArtifactsWindow();
            RefreshArtifactsPanel();
        }
        else
        {
            _artifactsWindow?.Hide();
        }
    }

    /// <summary>面板窗口惰性创建一次，Hide/Show 全程复用（与主窗口关闭=隐藏同款模式）。</summary>
    private ArtifactsPanelWindow EnsureArtifactsWindow()
    {
        if (_artifactsWindow is null)
        {
            _artifactsWindow = new ArtifactsPanelWindow();
            _artifactsWindow.CloseRequested += () => SetArtifactsPanel(false);
        }
        return _artifactsWindow;
    }

    /// <summary>主窗口可见性与面板开合状态对齐：主窗口隐藏 → 面板藏；主窗口显示且状态开 → 定位并显示。
    /// 该方法挂在 PropertyChanged 同步链路（主窗口 Show/Hide 都会经过），面板任何异常不得穿透打断主链路。</summary>
    private void SyncArtifactsWindow()
    {
        try
        {
            if (!IsVisible || !_artifactsPanelOpen)
            {
                _artifactsWindow?.Hide();
                return;
            }
            var panel = EnsureArtifactsWindow();   // 惰性创建（按钮点击首次从这里进）
            RepositionArtifactsWindow();
            if (!panel.IsVisible)
            {
                panel.Show();   // 普通窗口（不 owned、不 Topmost）：目标 z 序 = 面板恒在主窗口之下
            }
            // Windows 上对已激活窗口调 Activate() 是 no-op（不重排 z 序），panel.Show() 后面板在主窗口之上；
            // Topmost 瞬时翻转把主窗口强制提回面板之前（无焦点变化、无视觉闪烁），实现「MainWindow 压在面板上」
            Topmost = true;
            Topmost = false;
            RefreshArtifactsPanel();
        }
        catch (Exception ex)
        {
            // 面板是纯显示增强：脏数据/构建失败静默降级（不记疲劳日志，留 Trace 供排查）
            System.Diagnostics.Trace.WriteLine($"[ArtifactsPanel] sync failed: {ex}");
        }
    }

    /// <summary>
    /// 面板贴主窗口右缘外侧、与主窗口客户区同高，随主窗口移动/缩放/跨屏跟随；
    /// 右侧越出工作区改贴主窗口左缘，仍放不下则夹紧进工作区。
    /// 对齐一律用 PointToScreen（后端权威换算，内含 DPI/标题栏/Windows 不可见悬边语义）：
    /// 早期手算 Position+DIP×scaling 在 Windows 上曾致底部溢出主窗口 ~60px。
    /// </summary>
    private void RepositionArtifactsWindow()
    {
        if (_artifactsWindow is null) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var topLeft = Avalonia.VisualExtensions.PointToScreen(this, new Point(0, 0));                             // 客户区左上（像素）
        var bottomRight = Avalonia.VisualExtensions.PointToScreen(this, new Point(Bounds.Width, Bounds.Height));   // 客户区右下（像素）
        var pw = (int)Math.Ceiling(ArtifactsPanelWindow.PanelWidth * screen.Scaling);
        var pixelH = Math.Max(1, bottomRight.Y - topLeft.Y);
        var area = screen.WorkingArea;   // 物理像素
        PixelPoint pos;
        if (bottomRight.X + pw <= area.Right)
        {
            // 贴客户区右缘并向左压进 5px：面板衬在主窗口右后方，左缘被主窗口圆角压住（z 序面板 < 主窗口）
            pos = new PixelPoint(bottomRight.X - 5, topLeft.Y);
        }
        else if (topLeft.X - pw >= area.X)
        {
            pos = new PixelPoint(topLeft.X - pw, topLeft.Y);   // 右侧越界：贴左缘
        }
        else
        {
            var x = Math.Clamp(bottomRight.X, area.X, Math.Max(area.X, area.Right - pw));
            var y = Math.Clamp(topLeft.Y, area.Y, Math.Max(area.Y, area.Bottom - pixelH));
            pos = new PixelPoint(x, y);   // 两侧都放不下：夹紧进工作区
        }
        _artifactsWindow.Position = pos;
        _artifactsWindow.Height = pixelH / screen.Scaling;   // 面板与主窗口同屏：像素高还原 DIP 等高
    }

    /// <summary>按当前会话聚合产物刷新面板（面板未显示时跳过：展开入口必然触发一次）。
    /// 多由异步事件（ReadyChanged/ArtifactsReady 等）驱动，防御聚合层任何数据异常。</summary>
    private void RefreshArtifactsPanel()
    {
        try
        {
            if (_artifactsWindow is null || !_artifactsPanelOpen || !_artifactsWindow.IsVisible) return;
            _artifactsWindow.Refresh(_controller.GetSessionArtifacts());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[ArtifactsPanel] refresh failed: {ex}");
        }
    }
}
