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
    private bool _turnHadError;   // 本回合是否以异常结束（用于提示音判定）

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
    /// <summary>悬浮气泡宿主（审批/提问在对话窗非活动时的快捷提示面）。</summary>
    public BubbleWindow? Bubble { get; private set; }
    public void AttachBubble(BubbleWindow bubble) => Bubble = bubble;
    public void AttachLifetime(IClassicDesktopStyleApplicationLifetime lifetime) => _lifetime = lifetime;

    public MainWindow Conversation => _conversation ?? throw new InvalidOperationException("对话窗未创建");

    /// <summary>启动过程状态（连接 MCP / 恢复会话 / 就绪）：由顶栏状态胶囊展示，不进对话流。</summary>
    public event Action<string>? StartupStatus;

    /// <summary>会话列表/标题变化（切换、新建、删除、自动命名）后触发，UI 刷新会话下拉。</summary>
    public event Action? SessionsChanged;

    public string CurrentSessionTitle => Ready ? Agent!.CurrentSessionTitle : "新对话";
    public IReadOnlyList<(string Role, string Text)> GetTranscript() => Ready ? Agent!.GetTranscript() : [];
    public IReadOnlyList<SessionMeta> Sessions => Ready ? Agent!.Sessions : [];

    /// <summary>切换会话；忙碌/未就绪时返回 false。</summary>
    public async Task<bool> SwitchSessionAsync(string id)
    {
        if (Busy || !Ready) return false;
        var ok = await Agent!.SwitchSessionAsync(id);
        if (ok) RaiseState(BrookRunState.Idle, null);
        SessionsChanged?.Invoke();
        return ok;
    }

    public void RenameSession(string id, string title)
    {
        if (!Ready) return;
        Agent!.RenameSession(id, title);
        SessionsChanged?.Invoke();
    }

    public async Task DeleteSessionAsync(string id)
    {
        if (Busy || !Ready) return;
        await Agent!.DeleteSessionAsync(id);
        SessionsChanged?.Invoke();
    }

    /// <summary>把当前会话成果沉淀为技能（零 token：直接用滚动摘要+末次回答拼装）。</summary>
    public (string Name, string? Error) DistillSessionToSkill()
    {
        if (!Ready) return ("", "尚未就绪");
        var (name, error) = Agent!.DistillSessionToSkill();
        if (error is null) SessionsChanged?.Invoke();
        return (name, error);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables("AGENTBROOK_")
                .Build();
            var config = configuration.Get<AppConfig>() ?? new AppConfig();
            var appRoot = Workspace.LocateAppRoot();

            // ── 环境自检与自动初始化（此前打包到新机器后静默失败，卡在"启动中"） ──
            // 1) 应用数据目录可写性探针（打包发布后 appRoot 落在用户数据目录；开发期为源码目录）
            StartupStatus?.Invoke(AgentBrook.Agent.Infrastructure.CoreStrings.L(
                "检查应用环境…", "Checking environment…"));
            Directory.CreateDirectory(appRoot);
            var probe = Path.Combine(appRoot, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            // 2) 工作区：与 UiPrefs 共用同一解析规则（配置路径存在用之；全新安装回退数据目录内 workspace）
            var resolvedWs = Workspace.ResolveWorkspaceDir(appRoot, config.Agent.WorkspaceRoot);
            if (Path.GetFullPath(resolvedWs) != Path.GetFullPath(Path.Combine(appRoot, config.Agent.WorkspaceRoot)))
            {
                config.Agent.WorkspaceRoot = Path.GetRelativePath(appRoot, resolvedWs);
            }
            Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} config+workspace ok");

            Agent = await BrookAgent.CreateAsync(config, appRoot, interaction: this,
                log: line =>
                {
                    Console.Error.WriteLine($"[init-log] {DateTime.Now:HH:mm:ss.fff} {line}");
                    StartupStatus?.Invoke(line);
                    if (line.Contains('✖') || line.Contains("失败"))
                    {
                        // 失败必须在对话区可见（用户需要知道哪里坏了）；成功路径保持安静
                        EventRaised?.Invoke(new BrookEvent.ToolStarted("log:" + line, null));
                    }
                });

            Agent.SessionTitleChanged += _ => SessionsChanged?.Invoke();   // 自动命名等标题变化 → UI 刷新

            ReadyChanged?.Invoke();
            Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} ready");
            RaiseState(BrookRunState.Idle, null);
            Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} RaiseState(Idle) invoked");
            _ = GenerateGreetingAsync();   // 就绪后生成启动问候语（失败静默）
        }
        catch (Exception ex)
        {
            // 启动失败必须可见：此前异常被丢弃，应用会永远停在"启动中"
            Console.Error.WriteLine($"[init] ✖ 启动失败：{ex}");
            var msg = AgentBrook.Agent.Infrastructure.CoreStrings.L(
                $"✖ 启动失败：{ex.Message}", $"✖ Startup failed: {ex.Message}");
            StartupStatus?.Invoke(msg);
            EventRaised?.Invoke(new BrookEvent.ToolStarted("log:" + msg, null));
        }
    }

    /// <summary>启动问候语：用默认（摘要档）模型按当前时间/节假日生成，展示在气泡上。失败完全静默。</summary>
    private async Task GenerateGreetingAsync()
    {
        try
        {
            await Task.Delay(1500);   // 等气泡与首帧稳定
            if (Agent is null)
            {
                return;
            }
            var now = DateTime.Now;
            var period = now.Hour switch
            {
                >= 5 and < 9 => "清晨",
                < 12 => "上午",
                < 14 => "中午",
                < 18 => "下午",
                < 23 => "晚上",
                _ => "深夜",
            };
            var prompt = $"现在是 {now:yyyy年M月d日 dddd HH:mm}（{period}）。" +
                "请以助手的口吻用不超过 60 字的中文向用户打一句问候：结合时间段与（如有）临近或当天的重要节假日；" +
                "再自然地带一句你能帮什么忙（如查行情、写报告、跑脚本、整理文件）。直接输出问候语本身，不要引号、不要解释。";
            var text = await Agent.QuickCompleteAsync("你是桌面智能体 Brook 的问候语生成器。输出一条简短、自然、温暖、不油腻的中文问候。", prompt);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            Dispatcher.UIThread.Post(() =>
                Bubble?.ShowToast(text, "#4a9eff", TimeSpan.FromSeconds(15), openConversationOnClick: true));
        }
        catch
        {
            // 无网络 / 无 API Key / 模型报错：问候语属锦上添花，静默跳过
        }
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
        _turnHadError = false;
        UserSaid?.Invoke(text);
        RaiseState(BrookRunState.Thinking, null);

        var pendingTool = "";
        var sawTurnCompleted = false;
        var turnCancelled = false;
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
        catch (OperationCanceledException) when (Agent?.TurnCancelRequested == true)
        {
            turnCancelled = true;
            // 用户主动停止：不是错误。已完成部分保留，追加一条灰色日志行说明。
            EventRaised?.Invoke(new BrookEvent.ToolStarted("log:" +
                AgentBrook.Agent.Infrastructure.CoreStrings.L(
                    "⏹ 已按你的要求停止当前任务。",
                    "⏹ Stopped the current task at your request."), null));
        }
        catch (Exception ex)
        {
            _turnHadError = true;
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
            // 任务完成且对话窗非活动：提示音提醒用户回来看结果（用户主动停止/出错不响）
            var convActive = _conversation is { IsVisible: true, IsActive: true };
            if (sawTurnCompleted && !turnCancelled && !_turnHadError && !convActive
                && AssistantIdentity.NotifySoundOn)
            {
                NotifySound.Play(AssistantIdentity.NotifySoundId);
            }
        }
        DequeueAndSend();
    }

    private async void DequeueAndSend()
    {
        while (_queuedInput.Count > 0)
        {
            var next = _queuedInput.Dequeue();
            QueueChanged?.Invoke(_queuedInput.Count);
            await SendAsync(next);
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
        SessionsChanged?.Invoke();
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

    private readonly Queue<string> _queuedInput = new();
    public event Action<int>? QueueChanged;   // 队列深度变化（UI 显示排队数）

    /// <summary>打断当前回合（停止模型流，已完成部分保留）。</summary>
    public void CancelTurn() => Agent?.CancelTurn();

    /// <summary>排队一条输入：当前回合结束后自动发送（用户在模型工作中补充信息）。</summary>
    public void EnqueueInput(string text)
    {
        _queuedInput.Enqueue(text);
        QueueChanged?.Invoke(_queuedInput.Count);
    }

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
        LinkCancel(tcs, cancellationToken, () => { if (_approvalTcs == tcs) _approvalTcs = null; });
        RaiseState(BrookRunState.AwaitingApproval, toolName);
        // 对话窗正在前台：卡片就地交互；否则不拉窗抢焦点，改在气泡上给快捷批准/拒绝（卡片仍入对话流留档）
        var convActive = _conversation is { IsVisible: true, IsActive: true };
        Dispatcher.UIThread.Post(() =>
        {
            Conversation.AddApprovalCard(toolName, argumentsJson, tcs);
            if (!convActive)
            {
                Bubble?.ShowApprovalToast(toolName, argumentsJson, tcs);
                if (AssistantIdentity.NotifySoundOn)
                {
                    NotifySound.Play(AssistantIdentity.NotifySoundId);   // 需要用户批准：响铃提醒
                }
            }
            else
            {
                ShowConversation();
            }
        });
        return tcs.Task;
    }

    public Task<string> AskUserAsync(string question, string? choices, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _askTcs = tcs;
        LinkCancel(tcs, cancellationToken, () => { if (_askTcs == tcs) _askTcs = null; });
        RaiseState(BrookRunState.AwaitingUserAnswer, null);
        var convActive = _conversation is { IsVisible: true, IsActive: true };
        Dispatcher.UIThread.Post(() =>
        {
            Conversation.AddAskCard(question, choices, tcs);
            if (!convActive)
            {
                Bubble?.ShowAskToast(question, tcs);
                if (AssistantIdentity.NotifySoundOn)
                {
                    NotifySound.Play(AssistantIdentity.NotifySoundId);   // 需要用户回答：响铃提醒
                }
            }
            else
            {
                ShowConversation();
            }
        });
        return tcs.Task;
    }

    /// <summary>打断支持：停止令牌触发时令等待任务进入 Canceled 状态（回合随即以取消收尾）。</summary>
    private static void LinkCancel<T>(TaskCompletionSource<T> tcs, CancellationToken ct, Action? onCanceled = null)
    {
        if (!ct.CanBeCanceled)
        {
            return;
        }
        var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            onCanceled?.Invoke();
        });
        tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
    }
}
