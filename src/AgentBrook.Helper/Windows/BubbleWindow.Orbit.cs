using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using AgentBrook.Agent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Threading;

namespace AgentBrook.Helper.Windows;

// 轨道图标：工具/子Agent 执行期间绕气泡外圈旋转显示，按启动顺序排布、完成渐隐收场。
public partial class BubbleWindow
{
    /// <summary>轨道图标：工具/子Agent 执行期间绕气泡外圈显示，按启动顺序排布。</summary>
    private sealed record OrbitItem(string Tool, Border Chip);
    private readonly List<OrbitItem> _orbit = new();
    private const double OrbitRadius = 42;          // 轨道半径（本体缩至 0.72 后收近：42×0.9≈38dip，贴近气泡边缘）
    private const double OrbitChipSize = 20;
    private const double OrbitPeriodSeconds = 12;   // 绕圆一圈的时长
    private double _orbitPhase;                     // 当前环相位（弧度，0 = 第一个图标在正上方）
    private DispatcherTimer? _orbitTimer;

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
        UpdateWindowLayoutMode();   // 有轨道时退出紧凑模式；必须在入列之后调用——退出判定基于"轨道非空"，放前面首个图标会拿到空轨道而留在紧凑窗口，轨道图标被窗口边缘裁剪
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
        UpdateWindowLayoutMode();   // 轨道清空后可回到紧凑模式
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
        UpdateWindowLayoutMode();   // 轨道清空后可回到紧凑模式
    }

    /// <summary>按启动顺序绕圆排布：相位 0 时第一个在正上方，后续顺时针均分。</summary>
    private void LayoutOrbit()
    {
        for (var i = 0; i < _orbit.Count; i++)
        {
            var angle = -Math.PI / 2 + _orbitPhase + i * (2 * Math.PI / _orbit.Count);
            var x = BubbleHalf + OrbitRadius * Math.Cos(angle) - OrbitChipSize / 2;
            var y = BubbleHalf + OrbitRadius * Math.Sin(angle) - OrbitChipSize / 2;
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
}
