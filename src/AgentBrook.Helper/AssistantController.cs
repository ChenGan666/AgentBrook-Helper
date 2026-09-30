using AgentBrook.Helper;
using AgentBrook.Agent;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;

/// <summary>
/// 气泡 + 对话窗双宿主共享的智能体控制器：
/// 持有 BrookAgent，负责回合执行、全局状态广播与 HITL 卡片路由（需要人工介入时自动弹出对话窗）。
/// </summary>
public sealed class AssistantController : IBrookInteraction
{
    private MainWindow? _conversation;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private TaskCompletionSource<ApprovalDecision>? _approvalTcs;
    private TaskCompletionSource<string>? _askTcs;

    public BrookAgent? Agent { get; private set; }
    public bool Busy { get; private set; }
    /// <summary>回合序号（每 SendAsync 递增）：UI 用它区分新旧回合。</summary>
    public int TurnSeq { get; private set; }
    private string _lastAnswer = "";
    public bool Ready => Agent is not null;
    /// <summary>最近一个回合的完整回答文本（回合结束后有效）。</summary>
    public string FinalAnswer => Agent?.TurnAnswer ?? "";

    /// <summary>回合事件（文本增量 / 工具活动 / 错误），由对话窗订阅渲染。</summary>
    public event Action<BrookEvent>? EventRaised;
    /// <summary>全局运行状态变化，气泡与对话窗状态条订阅。</summary>
    public event Action<BrookRunState, string?>? StateChanged;
    /// <summary>用户说了一句什么（对话窗渲染用户气泡）。</summary>
    public event Action<string>? UserSaid;
    /// <summary>工具执行完成的结果预览（执行反馈）。</summary>
    public event Action<string>? ToolFeedback;
    /// <summary>核心就绪（MCP 连接完成）。</summary>
    public event Action? ReadyChanged;

    public void AttachConversation(MainWindow window) => _conversation = window;

    public MainWindow? ConversationWindow => _conversation;
    public void AttachLifetime(IClassicDesktopStyleApplicationLifetime lifetime) => _lifetime = lifetime;

    public MainWindow Conversation => _conversation ?? throw new InvalidOperationException("对话窗未创建");

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables("AGENTBROOK_")
            .Build();
        var config = configuration.Get<AppConfig>() ?? new AppConfig();
        var appRoot = Workspace.LocateAppRoot();
        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} config+workspace ok");

        Agent = await BrookAgent.CreateAsync(config, appRoot, interaction: this,
            log: line =>
            {
                Console.Error.WriteLine($"[init-log] {DateTime.Now:HH:mm:ss.fff} {line}");
                EventRaised?.Invoke(new BrookEvent.ToolStarted("log:" + line, null));
            });

        ReadyChanged?.Invoke();
        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} ready");
        RaiseState(BrookRunState.Idle, null);
        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} RaiseState(Idle) invoked");
    }

    public void Shutdown()
    {
        _lifetime?.Shutdown();
    }

    /// <summary>发送一条用户指令并跑完整个回合（含 HITL 路由）。</summary>
    public async Task SendAsync(string text)
    {
        if (Busy || !Ready || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        Busy = true;
        TurnSeq++;
        _lastAnswer = "";
        UserSaid?.Invoke(text);
        RaiseState(BrookRunState.Thinking, null);

        var pendingTool = "";
        var sawTurnCompleted = false;
        try
        {
            await foreach (var e in Agent!.RunAsync(text))
            {
                switch (e)
                {
                    case BrookEvent.ToolStarted t when t.Name != "ask_user" && !t.Name.StartsWith("log:"):
                        pendingTool = t.Name;
                        RaiseState(BrookRunState.CallingTool, t.Name);
                        break;
                    case BrookEvent.ToolStarted t when t.Name == "ask_user":
                        pendingTool = t.Name;
                        RaiseState(BrookRunState.AwaitingUserAnswer, null);
                        break;
                    case BrookEvent.ToolCompleted tc:
                        ToolFeedback?.Invoke($"{tc.Name}：{(string.IsNullOrEmpty(tc.ResultPreview) ? "完成" : tc.ResultPreview)}");
                        break;
                }
                if (e is BrookEvent.TurnCompleted)
                {
                    sawTurnCompleted = true;   // Core 已发过，finally 不再补发（否则 UI 收到两次）
                }
                EventRaised?.Invoke(e);
            }
        }
        catch (Exception ex)
        {
            EventRaised?.Invoke(new BrookEvent.Failed(ex.Message));
        }
        finally
        {
            Busy = false;
            RaiseState(BrookRunState.Idle, null);
            if (!sawTurnCompleted)
            {
                EventRaised?.Invoke(new BrookEvent.TurnCompleted());   // 异常终止时保底
            }
        }
    }

    public async Task NewSessionAsync()
    {
        if (Busy || !Ready)
        {
            return;
        }
        await Agent!.NewSessionAsync();
        EventRaised?.Invoke(new BrookEvent.ToolStarted("__clear__", null));
    }

    public void CycleModel()
    {
        if (Agent is null || Agent.Models.Count < 2)
        {
            return;
        }
        var models = Agent.Models;
        var index = -1;
        for (var i = 0; i < models.Count; i++)
        {
            if (models[i] == Agent.CurrentModel)
            {
                index = i;
                break;
            }
        }
        Agent.SetModel(models[(index + 1) % models.Count]);
        RaiseState(BrookRunState.Idle, null);
    }

    public void ShowConversation()
    {
        var window = Conversation;
        window.Show();
        window.Activate();
    }

    private void RaiseState(BrookRunState state, string? detail) =>
        Dispatcher.UIThread.Post(() => StateChanged?.Invoke(state, detail));

    // ───────────────────────── HITL：需要人工时自动弹出对话窗 ─────────────────────────
    /// <summary>完全访问模式：所有需审批的操作自动通过（会话内有效，重启后复位）。</summary>
    public bool FullAccess { get; set; }

    public string CurrentProvider => Ready ? Agent!.CurrentProvider : "";
    public IReadOnlyList<AgentBrook.Agent.Infrastructure.ProviderConfig> Providers => Ready ? Agent!.Providers : [];

    public Task SetProviderAsync(string name) => Agent!.SwitchProviderAsync(name);

    public Task SaveProvidersAsync(List<AgentBrook.Agent.Infrastructure.ProviderConfig> providers, string activeProvider)
        => Agent!.SaveProvidersAsync(providers, activeProvider);

    /// <summary>应用助手个性化（名字/语言/默认提示词）。未就绪时静默跳过（下次启动设置窗口可再应用）。</summary>
    public async Task ApplyAssistantSettingsAsync(string name, string language, string customPrompt)
    {
        if (Ready)
        {
            await Agent!.ApplyAssistantSettingsAsync(name, language, customPrompt);
        }
    }

    public Task<ApprovalDecision> GetApprovalAsync(string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
    {
        // 完全访问模式：自动通过，仅在对话流中留痕
        if (FullAccess)
        {
            EventRaised?.Invoke(new BrookEvent.ToolStarted("log:已自动批准（完全访问）：" + toolName, argumentsJson));
            return Task.FromResult(ApprovalDecision.Approve());
        }
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalTcs = tcs;
        RaiseState(BrookRunState.AwaitingApproval, toolName);
        Dispatcher.UIThread.Post(() =>
        {
            ShowConversation();
            Conversation.AddApprovalCard(toolName, argumentsJson, tcs);
        });
        return tcs.Task;
    }

    public Task<string> AskUserAsync(string question, string? choices, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _askTcs = tcs;
        RaiseState(BrookRunState.AwaitingUserAnswer, null);
        Dispatcher.UIThread.Post(() =>
        {
            ShowConversation();
            Conversation.AddAskCard(question, choices, tcs);
        });
        return tcs.Task;
    }
}
