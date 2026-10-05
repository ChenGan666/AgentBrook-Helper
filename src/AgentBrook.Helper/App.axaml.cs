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
            var conversation = new MainWindow();
            Controller.AttachConversation(conversation);
            Controller.AttachLifetime(desktop);

            desktop.MainWindow = new BubbleWindow();
            // --show：启动即打开对话窗（免找气泡，也便于自动化自测）
            if (desktop.Args is not null && desktop.Args.Contains("--show"))
            {
                conversation.ShowFromBubble();
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
                };
                timer.Start();
            }
            _ = Controller.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
