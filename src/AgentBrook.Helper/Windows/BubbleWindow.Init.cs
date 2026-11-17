using AgentBrook.Helper.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentBrook.Helper.Windows;

// 启动初始化等待特效（气泡边缘绕圆进度）：
// · 气泡先于 Controller.InitializeAsync 创建显示（App.axaml.cs），插件/MCP/会话恢复期间无反馈
//   → 边缘挂淡色轨道 + 旋转弧段；悬停提示"初始化中"（I18n 三语）
// · 终态通知走 Controller.StartupFinished（就绪/需配置模型/启动失败三出口都会触发——
//   不能只订 ReadyChanged，缺模型配置早退与启动异常两条路径不触发它）→ 结束特效并清除提示
// · 弧段逐帧重建 StreamGeometry（BeginFigure+ArcTo），不依赖 RotateTransform 语义
//   （Visual.cs/Orbit.cs 既有教训）；初始化期间交互门卫见 axaml.cs 与 Voice.cs
public partial class BubbleWindow
{
    private Ellipse _initTrack = null!;
    // 全限定 Shapes.Path：隐式 System.IO 的 Path 会让短名二义（同 axaml.cs 的处理）
    private Avalonia.Controls.Shapes.Path _initRing = null!;
    private bool _initializing;
    private double _initRingAngle;
    private DispatcherTimer? _initRingTimer;

    // 几何常量：80×80 框内圆心 (40,40)，半径 38.5（扣半描边，描边外缘恰贴框边）
    private const double InitRingCenter = 40.0;
    private const double InitRingRadius = 38.5;
    private const double InitRingSweepDegrees = 110.0;   // 弧长约 1/3 圈
    private const double InitRingStepDegrees = 6.0;      // 33ms×6° ≈ 2 秒/圈
    private const int InitRingFrameIntervalMs = 33;

    /// <summary>构造函数调用：订阅终态/语言事件；未就绪即开始等待特效。</summary>
    private void InitStartupGate()
    {
        _initTrack = this.FindControl<Ellipse>("InitTrack")!;
        _initRing = this.FindControl<Avalonia.Controls.Shapes.Path>("InitRing")!;

        _controller.StartupFinished += () => Dispatcher.UIThread.Post(EndInitializing);
        I18n.LanguageChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_initializing)
            {
                ToolTip.SetTip(_root, I18n.T("bubble.initializing"));   // 初始化期间切语言即时刷新
            }
        });

        if (!_controller.Ready)
        {
            BeginInitializing();   // 气泡先于 InitializeAsync 创建，此时必未就绪（防御性判断）
        }
    }

    /// <summary>初始化开始：显示等待环，悬停提示"初始化中"，启动逐帧弧段动画。</summary>
    private void BeginInitializing()
    {
        if (_initializing)
        {
            return;
        }
        _initializing = true;
        _initRingAngle = 0;
        _initTrack.IsVisible = true;
        _initRing.IsVisible = true;
        ToolTip.SetTip(_root, I18n.T("bubble.initializing"));

        if (_initRingTimer is null)
        {
            _initRingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InitRingFrameIntervalMs) };
            _initRingTimer.Tick += (_, _) =>
            {
                _initRingAngle = (_initRingAngle + InitRingStepDegrees) % 360.0;
                _initRing.Data = BuildInitRingGeometry(_initRingAngle);
            };
        }
        _initRing.Data = BuildInitRingGeometry(_initRingAngle);
        if (!_initRingTimer.IsEnabled)
        {
            _initRingTimer.Start();
        }
    }

    /// <summary>初始化结束（就绪/需配置模型/启动失败）：停动画、隐藏环、清除悬停提示。</summary>
    private void EndInitializing()
    {
        if (!_initializing)
        {
            return;
        }
        _initializing = false;
        if (_initRingTimer is { IsEnabled: true })
        {
            _initRingTimer.Stop();
        }
        _initTrack.IsVisible = false;
        _initRing.IsVisible = false;
        ToolTip.SetTip(_root, null);
    }

    /// <summary>生成从 startAngle 起、定长 sweep 的顺时针弧段几何（每帧重建，形状真实变化）。</summary>
    private static StreamGeometry BuildInitRingGeometry(double startAngle)
    {
        var startRad = startAngle * Math.PI / 180.0;
        var endRad = (startAngle + InitRingSweepDegrees) * Math.PI / 180.0;
        var p0 = new Point(InitRingCenter + InitRingRadius * Math.Cos(startRad),
                           InitRingCenter + InitRingRadius * Math.Sin(startRad));
        var p1 = new Point(InitRingCenter + InitRingRadius * Math.Cos(endRad),
                           InitRingCenter + InitRingRadius * Math.Sin(endRad));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p0, false);
            ctx.ArcTo(p1, new Size(InitRingRadius, InitRingRadius), 0,
                InitRingSweepDegrees > 180.0, SweepDirection.Clockwise);
        }
        return geo;
    }
}
