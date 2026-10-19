using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;


namespace AgentBrook.Helper;

public partial class App : Application
{
    /// <summary>全局共享的智能体控制器（气泡与对话窗共用）。</summary>
    public static AssistantController Controller { get; private set; } = null!;
    public static A2AExtensionHost A2A { get; private set; } = null!;
    private static A2ASessionWindow? _a2aWindow;

    /// <summary>是否正在处理请求（本地回合或 A2A 远程消息）：系统退出请求据此决定放行或阻止。</summary>
    public static bool IsProcessing => Controller.Busy || A2A.Controller.IsRemoteBusy;

    /// <summary>空闲时收到系统退出请求即置位；气泡/对话窗 OnClosing 据此放行真正关闭。</summary>
    public static bool IsQuitting { get; private set; }

    public static void ShowA2AWindow()
    {
        try
        {
            if (_a2aWindow is null)
            {
                _a2aWindow = new A2ASessionWindow();
                _a2aWindow.Closed += (_, _) => _a2aWindow = null;
            }

            if (!_a2aWindow.IsVisible)
            {
                _a2aWindow.Show();
            }

            _a2aWindow.Activate();
            _a2aWindow.Focus();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[a2a] ShowA2AWindow failed: {ex}");
        }
    }

    public override void Initialize()
    {
        // UI 线程未处理异常兜底：记录日志并拦截，避免整个应用崩溃
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Console.Error.WriteLine($"[ui-unhandled] {e.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[fatal] {e.ExceptionObject}");
        };
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 气泡常驻；只有气泡右键菜单“退出”才会结束应用
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AssistantIdentity.Load();
            Controller = new AssistantController();
            A2A = new A2AExtensionHost(Controller);
            desktop.Exit += async (_, _) =>
            {
                try { await A2A.DisposeAsync(); }
                catch (Exception ex) { Console.Error.WriteLine($"[a2a] shutdown failed: {ex}"); }
            };
            _ = A2A.StartAsync();

            // 系统关机/注销/Cmd+Q：空闲则置 IsQuitting 放行窗口真正关闭、正常退出；
            // 正在处理请求则取消退出，让 macOS 弹「中断了关机」提示，回合不被打断
            desktop.ShutdownRequested += (_, e) =>
            {
                if (IsProcessing)
                {
                    e.Cancel = true;
                    Controller.Bubble?.ShowToast(I18n.T("ui.shutdown.busy"), "#ffb340", TimeSpan.FromSeconds(5));
                }
                else
                {
                    IsQuitting = true;
                }
            };

            var conversation = new MainWindow();
            Controller.AttachConversation(conversation);
            Controller.AttachLifetime(desktop);
            var bubble = new BubbleWindow();
            Controller.AttachBubble(bubble);
            desktop.MainWindow = bubble;

            // 每日首次启动检查更新（发现新版本时气泡提醒，可一键转设置自动更新）
            _ = Controller.CheckUpdateOnStartupAsync();

            // 首次启动初始化向导（新安装弹出；已配置模型的旧安装自动跳过）
            if (OnboardingWindow.IsNeeded() || (desktop.Args is not null && desktop.Args.Contains("--onboarding")))
            {
                var onboarding = new OnboardingWindow();
                onboarding.Show();
            }

            // --fullaccess：完全访问模式启动（审批自动通过，测试钩子）
            if (desktop.Args is not null && desktop.Args.Contains("--fullaccess"))
            {
                Controller.FullAccess = true;
            }

            // --settings：就绪后自动打开设置窗口（端到端测试钩子）
            if (desktop.Args is not null && desktop.Args.Contains("--settings"))
            {
                var stTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                stTimer.Tick += (_, _) =>
                {
                    if (!Controller.Ready)
                    {
                        return;
                    }
                    stTimer.Stop();
                    var settings = new ModelSettingsWindow();
                    settings.Show();
                };
                stTimer.Start();
            }
            // --show：启动即打开对话窗（免找气泡，也便于自动化自测）
            if (desktop.Args is not null && desktop.Args.Contains("--show"))
            {
                conversation.ShowFromBubble();
            }
            if (desktop.Args is not null && desktop.Args.Contains("--a2a-show"))
            {
                ShowA2AWindow();
            }
            if (desktop.Args is not null && desktop.Args.Contains("--langtest"))
            {
                var lt = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                lt.Tick += async (_, _) =>
                {
                    lt.Stop();
                    if (!App.Controller.Ready)
                    {
                        lt.Start();
                        return;
                    }
                    System.Console.Error.WriteLine("[langtest] ready, applying en settings");
                    await App.Controller.ApplyAssistantSettingsAsync("HiBrook", "en", "");
                    System.Console.Error.WriteLine("[langtest] applied, sending hello");
                    await App.Controller.SendAsync("hello");
                };
                lt.Start();
            }
            if (desktop.Args is not null && desktop.Args.Contains("--uilang-en"))
            {
                var ul = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                ul.Tick += (_, _) =>
                {
                    ul.Stop();
                    var win = new ModelSettingsWindow();
                    win.Show();
                    win.DebugSelectUiLanguage("en");
                };
                ul.Start();
            }
            if (desktop.Args is not null && desktop.Args.Contains("--pastetest"))
            {
                var pasteTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                pasteTimer.Tick += (_, _) =>
                {
                    pasteTimer.Stop();
                    _ = conversation.DebugPasteAsync();
                };
                pasteTimer.Start();
            }
            if (desktop.Args is not null && desktop.Args.Contains("--cardtest"))
            {
                // 审批卡视觉验证：用样例数据直接构造（真实 JSON，含 \uXXXX 转义）
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    conversation.AddApprovalCard("run_command",
                        "{\"command\":\"open reports/gold/gold-report-20260930.html \\u0026\\u0026 echo \\u0022已用系统默认浏览器打开\\u0022\"}",
                        new System.Threading.Tasks.TaskCompletionSource<AgentBrook.Agent.ApprovalDecision>());
                    conversation.AddAskCard("文件想放在哪里？",
                        "重新复制到桌面|就放工作区|放到其他目录",
                        new System.Threading.Tasks.TaskCompletionSource<string>());
                    conversation.DebugSeedCardInputs();   // 灌入超长文本验证输入框固定宽度自动换行
                };
                timer.Start();
            }
            // --say "文本"：就绪后自动发送一条消息（端到端测试钩子，模拟用户输入）
            if (desktop.Args is not null)
            {
                var sayIdx = Array.FindIndex(desktop.Args, a => a == "--say");
                if (sayIdx >= 0 && sayIdx + 1 < desktop.Args.Length)
                {
                    var sayText = desktop.Args[sayIdx + 1];
                    var sayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    sayTimer.Tick += async (_, _) =>
                    {
                        if (!Controller.Ready)
                        {
                            return;   // 未就绪：定时器继续等待
                        }
                        sayTimer.Stop();
                        Console.Error.WriteLine($"[say] 发送测试消息：{sayText}");
                        await Controller.SendAsync(sayText);
                    };
                    sayTimer.Start();
                }
            }
            // --fakepaste：就绪后模拟一次 Ctrl/Cmd+V 按键路由事件（粘贴链路的端到端测试钩子）
            if (desktop.Args is not null && desktop.Args.Contains("--fakepaste"))
            {
                var fpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                fpTimer.Tick += (_, _) =>
                {
                    fpTimer.Stop();
                    if (App.Controller.ConversationWindow is MainWindow mw)
                    {
                        mw.RaisePasteKeyEvent();
                    }
                };
                fpTimer.Start();
            }
            _ = Controller.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnAboutClick(object? sender, System.EventArgs e)
    {
        var about = new AboutWindow();
        about.Show();
    }
}
