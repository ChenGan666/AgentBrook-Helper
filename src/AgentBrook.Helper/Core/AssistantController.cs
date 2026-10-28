using AgentBrook.Helper.Support;
using AgentBrook.Helper.Windows;
using AgentBrook.Helper.Views;
using AgentBrook.Helper;
using AgentBrook.Agent;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using AgentBrook.Helper.Update;

namespace AgentBrook.Helper.Core;

/// <summary>
/// 气泡 + 对话窗双宿主共享的智能体控制器：
/// 持有 BrookAgent，负责回合执行、全局状态广播与 HITL 卡片路由（需要人工介入时自动弹出对话窗）。
/// </summary>
/// <summary>更新偏好（update-prefs.json）。</summary>
internal sealed record UpdatePrefs(bool DailyCheck = true, bool AutoDownload = true);

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
    public event Action? ModelsChanged;
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

    private sealed record UpdateCheckState(string LastCheckDate);


    /// <summary>
    /// 启动时每日一次检查更新（当天已检查过则跳过，标记持久化于工作区 update-check.json）。
    /// 发现新版本 → 气泡提醒（新版本号 + 更新说明）；用户点「立即更新」→ 打开设置窗口
    /// 定位到版本更新区并自动开始更新。检查失败不影响主流程。
    /// </summary>
    public async Task CheckUpdateOnStartupAsync()
    {
        try
        {
            var prefs = UiPrefs.Load<UpdatePrefs>("update-prefs.json") ?? new UpdatePrefs();
            if (!prefs.DailyCheck)
            {
                return;   // 用户关闭了每日自动检查更新
            }
            var state = UiPrefs.Load<UpdateCheckState>("update-check.json");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (state?.LastCheckDate == today)
            {
                return;   // 今天已检查过：每日仅首次启动检查
            }
            UiPrefs.Save("update-check.json", new UpdateCheckState(today));

            // 等待助手就绪（最长 2 分钟）；超时放弃本轮，明天再查
            var waited = TimeSpan.Zero;
            while (!Ready && waited < TimeSpan.FromMinutes(2))
            {
                await Task.Delay(1000);
                waited += TimeSpan.FromSeconds(1);
            }
            if (!Ready)
            {
                return;
            }

            var info = await AutoUpdater.CheckAsync();
            if (info is null || !AutoUpdater.IsNewer(info.Version))
            {
                return;
            }

            var notes = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "" : $"：{info.ReleaseNotes.Trim()}";
            if (notes.Length > 120)
            {
                notes = notes[..120] + "…";
            }
            var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 等气泡出现（启动初期可能尚未创建，最长 30 秒）
            var bubbleWait = TimeSpan.Zero;
            while (Bubble is null && bubbleWait < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(1000);
                bubbleWait += TimeSpan.FromSeconds(1);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Bubble is null)
                {
                    approval.TrySetResult(true);   // 气泡缺失：降级为直接打开设置更新
                    return;
                }
                Bubble.ShowToast(
                    $"发现新版本 {info.Version}{notes}",
                    "#4a9eff",
                    autoClose: TimeSpan.FromMinutes(10),
                    actions: new[]
                    {
                        (I18n.T("ui.update.now"), (Action)(() => approval.TrySetResult(true))),
                        (I18n.T("ui.later"), (Action)(() => approval.TrySetResult(false)))
                    },
                    openConversationOnClick: false);
            });
            if (await approval.Task)
            {
                OpenSettingsForUpdate(info, prefs.AutoDownload);
            }
        }
        catch
        {
            // 更新检查/提醒失败不影响主流程
        }
    }

    /// <summary>打开设置窗口，定位到版本更新区并自动开始更新（气泡"立即更新"入口）。</summary>
    internal void OpenSettingsForUpdate(UpdateInfo info, bool autoStart = true)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var settings = ModelSettingsWindow.ShowSingle();
            _ = settings.StartUpdateFlowAsync(info, autoStart);
        });
    }

    public void AttachLifetime(IClassicDesktopStyleApplicationLifetime lifetime) => _lifetime = lifetime;

    public MainWindow Conversation => _conversation ?? throw new InvalidOperationException("对话窗未创建");

    /// <summary>启动过程状态（连接 MCP / 恢复会话 / 就绪）：由顶栏状态胶囊展示，不进对话流。</summary>
    public event Action<string>? StartupStatus;

    /// <summary>会话列表/标题变化（切换、新建、删除、自动命名）后触发，UI 刷新会话下拉。</summary>
    public event Action? SessionsChanged;

    public string CurrentSessionTitle => Ready ? Agent!.CurrentSessionTitle : "新对话";
    public IReadOnlyList<(string Role, string Text)> GetTranscript() => Ready ? Agent!.GetTranscript() : [];
    /// <summary>滚动加载历史用：可读记录总条数与分页读取（skipFromEnd=从末尾跳过的条数）。</summary>
    public int TranscriptCount => Ready ? Agent!.TranscriptCount : 0;
    public IReadOnlyList<(string Role, string Text)> GetTranscriptRange(int skipFromEnd, int take) =>
        Ready ? Agent!.GetTranscriptRange(skipFromEnd, take) : [];
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

    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    public bool ModelConfigurationRequired { get; private set; }
    public event Action? ModelConfigurationChanged;

    internal static AppConfig LoadStartupConfig() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddEnvironmentVariables("AGENTBROOK_")
        .Build().Get<AppConfig>() ?? new AppConfig();

    internal static ProviderSettings LoadProviders(AppConfig config, string workspaceDir)
    {
        var settings = ProviderSettings.Load(Path.Combine(workspaceDir, "models.json"));
        settings.EnsureDefaultProvider(config.LLM);
        return settings;
    }

    internal static bool HasUsableChatModel(ProviderSettings settings) => settings.HasUsableChatModel();

    private void SetModelConfigurationRequired(bool required)
    {
        if (ModelConfigurationRequired == required) return;
        ModelConfigurationRequired = required;
        ModelConfigurationChanged?.Invoke();
    }

    public async Task InitializeAsync()
    {
        await _initializationGate.WaitAsync();
        try
        {
            if (!Ready) await InitializeCoreAsync();
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            var config = LoadStartupConfig();
            var appRoot = Workspace.LocateAppRoot();

            // ── 环境自检与自动初始化（此前打包到新机器后静默失败，卡在"启动中"） ──
            // 1) 应用数据目录可写性探针（打包发布后 appRoot 落在用户数据目录；开发期为源码目录）
            StartupStatus?.Invoke(AgentBrook.Agent.Infrastructure.CoreStrings.L(
                "检查应用环境…", "Checking environment…", "Memeriksa lingkungan aplikasi…"));
            Directory.CreateDirectory(appRoot);
            var probe = Path.Combine(appRoot, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            // 0) 用户自定义主工作空间（设置中配置，重启应用生效）
            var wsOverride = WorkspacePrefs.LoadOverride();
            if (!string.IsNullOrWhiteSpace(wsOverride))
            {
                Directory.CreateDirectory(wsOverride);
                config.Agent.WorkspaceRoot = wsOverride;
            }

            // 2) 工作区：与 UiPrefs 共用同一解析规则（配置路径存在用之；全新安装回退数据目录内 workspace）
            var resolvedWs = Workspace.ResolveWorkspaceDir(appRoot, config.Agent.WorkspaceRoot);
            if (Path.GetFullPath(resolvedWs) != Path.GetFullPath(Path.Combine(appRoot, config.Agent.WorkspaceRoot)))
            {
                config.Agent.WorkspaceRoot = Path.GetRelativePath(appRoot, resolvedWs);
            }
            Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} config+workspace ok");

            if (!HasUsableChatModel(LoadProviders(config, resolvedWs)))
            {
                SetModelConfigurationRequired(true);
                StartupStatus?.Invoke(I18n.T("ui.model.configuration.required"));
                return;
            }
            SetModelConfigurationRequired(false);

            // A2A 协作工具（晚绑定 App.A2A：组合根先创建 Controller 再创建 A2A 宿主）
            var a2aTools = new A2AAgentTools(() => App.A2A?.Controller);

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
                },
                additionalTools: a2aTools.BuildTools(),
                additionalInstructions: A2AAgentTools.Instructions);

            Agent.SessionTitleChanged += _ => SessionsChanged?.Invoke();   // 自动命名等标题变化 → UI 刷新

            ReadyChanged?.Invoke();
            Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} ready");
            RaiseState(BrookRunState.Idle, null);
            Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} RaiseState(Idle) invoked");
            _ = GenerateGreetingAsync();   // 就绪后生成启动问候语（失败静默）
            _ = AutoRefreshModelListsAsync();   // 后台拉取各供应商模型列表（静默，仅变化时气泡提示）
        }
        catch (Exception ex)
        {
            // 启动失败必须可见：此前异常被丢弃，应用会永远停在"启动中"
            Console.Error.WriteLine($"[init] ✖ 启动失败：{ex}");
            var msg = AgentBrook.Agent.Infrastructure.CoreStrings.L(
                $"✖ 启动失败：{ex.Message}", $"✖ Startup failed: {ex.Message}", $"✖ Startup gagal: {ex.Message}");
            StartupStatus?.Invoke(msg);
            EventRaised?.Invoke(new BrookEvent.ToolStarted("log:" + msg, null));
        }
    }

    /// <summary>启动问候语：用默认（摘要档）模型按当前时间/节假日生成，展示在气泡上。
    /// 模型调用失败或返回空时使用本地兜底问候，确保气泡下方始终出现问候卡。</summary>
    private async Task GenerateGreetingAsync()
    {
        string? text = null;
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
            text = await Agent.QuickCompleteAsync("你是桌面智能体 Brook 的问候语生成器。输出一条简短、自然、温暖、不油腻的中文问候。", prompt);
        }
        catch
        {
            // 无网络 / 无 API Key / 模型报错：走兜底问候
        }

        if (string.IsNullOrWhiteSpace(text))
        {
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
            text = $"{period}好！我是 {AssistantIdentity.Current}，可以帮你查行情、写报告、跑脚本或整理文件。";
        }

        Dispatcher.UIThread.Post(() =>
            Bubble?.ShowToast(text, "#4a9eff", TimeSpan.FromSeconds(15), openConversationOnClick: true));
    }

    public void Shutdown()
    {
        _lifetime?.Shutdown();
    }

    /// <summary>发送一条用户指令并跑完整个回合（含 HITL 路由）。</summary>
    public async Task SendAsync(string text) => await SendAsync(new ChatMessage(ChatRole.User, text));

    /// <summary>发送一条用户消息（支持文本 + 图片 DataContent 的多模态输入）。</summary>
    public async Task SendAsync(ChatMessage message)
    {
        if (Busy || !Ready)
        {
            return;
        }
        var text = message.Text ?? "";
        if (string.IsNullOrWhiteSpace(text) && message.Contents.Count == 0)
        {
            return;
        }
        Busy = true;
        TurnSeq++;
        _lastAnswer = "";
        _turnHadError = false;
        UserSaid?.Invoke(text);

        // 回合前置校验：Key 为占位符/非法时直接给出明确提示，不发必然失败的请求
        var keyError = Agent!.ValidateActiveProvider();
        if (keyError is not null)
        {
            Console.Error.WriteLine($"[turn] ✖ {keyError}");
            EventRaised?.Invoke(new BrookEvent.Failed(keyError));
            RaiseState(BrookRunState.Idle, null);
            DequeueAndSend();
            return;
        }
        RaiseState(BrookRunState.Thinking, null);

        var pendingTool = "";
        var sawTurnCompleted = false;
        var turnCancelled = false;
        try
        {
            await foreach (var e in Agent!.RunAsync(message))
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
            Console.Error.WriteLine("[turn] ⏹ 用户停止");
            // 用户主动停止：不是错误。已完成部分保留，追加一条灰色日志行说明。
            EventRaised?.Invoke(new BrookEvent.ToolStarted("log:" +
                AgentBrook.Agent.Infrastructure.CoreStrings.L(
                    "⏹ 已按你的要求停止当前任务。",
                    "⏹ Stopped the current task at your request.",
                    "⏹ Tugas dihentikan sesuai permintaan Anda."), null));
        }
        catch (Exception ex)
        {
            _turnHadError = true;
            Console.Error.WriteLine($"[turn] ✖ {ex.Message}");
            EventRaised?.Invoke(new BrookEvent.Failed(ex.Message));
        }
        finally
        {
            Busy = false;
            Console.Error.WriteLine($"[turn] 结束（{(sawTurnCompleted ? "正常" : "异常终止")}，回答 {(Agent?.TurnAnswer.Length ?? 0)} 字符）");
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
            var next = _queuedInput[0];
            _queuedInput.RemoveAt(0);
            QueueChanged?.Invoke(_queuedInput.Count);
            await SendAsync(next);
        }
    }

    /// <summary>排队纯文本输入（兼容性入口）。</summary>
    public void EnqueueInput(string text) => EnqueueInput(new ChatMessage(ChatRole.User, text));

    /// <summary>清空排队中的输入（用户点击排队条上的删除按钮）。</summary>
    public void ClearQueuedInput()
    {
        _queuedInput.Clear();
        QueueChanged?.Invoke(_queuedInput.Count);
    }

    /// <summary>删除指定一条排队消息。</summary>
    public void RemoveQueuedAt(int index)
    {
        if (index < 0 || index >= _queuedInput.Count) return;
        _queuedInput.RemoveAt(index);
        QueueChanged?.Invoke(_queuedInput.Count);
    }

    /// <summary>排队消息快照（UI 逐条渲染用；返回副本避免枚举期被改）。</summary>
    public IReadOnlyList<ChatMessage> QueuedInputs => _queuedInput.ToList();

    /// <summary>队首消息预览文本（排队条单行展示用）。</summary>
    public string? QueuedInputPreview =>
        _queuedInput.Count == 0 ? null : (_queuedInput[0].Text ?? "");

    /// <summary>
    /// 「立即执行」：指定排队消息插队——回合进行中注入为 steering 消息（Agent 自行判断补充/新任务），
    /// 空闲时等同直接发送。
    /// </summary>
    public void SteerQueuedInput() => RunQueuedAt(0);

    /// <summary>插队指定一条排队消息（供每条排队块的"立即执行"按钮）。</summary>
    public void RunQueuedAt(int index)
    {
        if (index < 0 || index >= _queuedInput.Count || !Ready) return;
        var msg = _queuedInput[index];
        _queuedInput.RemoveAt(index);
        QueueChanged?.Invoke(_queuedInput.Count);
        if (Busy && Agent is not null)
        {
            UserSaid?.Invoke(msg.Text ?? "");
            Agent.EnqueueSteering(msg);
        }
        else
        {
            _ = SendAsync(msg);
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

    /// <summary>
    /// A2A 远程自主处理的专用状态通道（用户补充要求：主会话界面只有用户使用，
    /// 被调用过程不得出现在主会话；气泡延用原有旋转状态给出反馈）。
    /// 只有 BubbleWindow 订阅这两个事件；MainWindow/执行过程面板不订阅。
    /// </summary>
    public event Action<BrookRunState, string?>? RemoteStateChanged;

    public event Action<AgentBrook.Agent.BrookEvent>? RemoteEventRaised;

    public void PublishRemoteActivity(BrookRunState state, string? detail) =>
        Dispatcher.UIThread.Post(() => RemoteStateChanged?.Invoke(state, detail));

    /// <summary>远程工具开始：气泡轨道添加图标（与本地 ToolStarted/ToolCompleted 按名字配对移除）。</summary>
    public void PublishRemoteToolStarted(string toolName) =>
        Dispatcher.UIThread.Post(() => RemoteEventRaised?.Invoke(new AgentBrook.Agent.BrookEvent.ToolStarted(toolName, null)));

    /// <summary>远程工具完成：移除对应轨道图标。</summary>
    public void PublishRemoteToolCompleted(string toolName) =>
        Dispatcher.UIThread.Post(() => RemoteEventRaised?.Invoke(new AgentBrook.Agent.BrookEvent.ToolCompleted(toolName, null)));

    /// <summary>远程回合结束：兜底清空全部轨道图标（本地 TurnCompleted 同语义）。</summary>
    public void PublishRemoteTurnCompleted() =>
        Dispatcher.UIThread.Post(() => RemoteEventRaised?.Invoke(new AgentBrook.Agent.BrookEvent.TurnCompleted()));

    /// <summary>
    /// 远程处理的工具审批：与本地审批同策略（FullAccess/RequireApprovalTools），
    /// 但呈现走气泡 toast + 远程状态（不进主会话卡片）。
    /// </summary>
    public async Task<ApprovalDecision> GetRemoteApprovalAsync(string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
    {
        if (FullAccess)
        {
            return ApprovalDecision.Approve();
        }
        if (Bubble is null)
        {
            return ApprovalDecision.Reject("本机审批界面不可用");
        }
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        RaiseRemoteApproval(tcs, toolName, argumentsJson);
        return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(8), cancellationToken);
    }

    private void RaiseRemoteApproval(TaskCompletionSource<ApprovalDecision> tcs, string toolName, string? argsJson)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RemoteStateChanged?.Invoke(BrookRunState.AwaitingApproval, toolName);
            var summary = Conversation?.SummarizeApprovalPublic(toolName, argsJson) ?? toolName;
            Bubble?.ShowRemoteApprovalToast(summary, tcs);
            if (AssistantIdentity.NotifySoundOn)
            {
                NotifySound.Play(AssistantIdentity.NotifySoundId);
            }
        });
    }

    // ───────────────────────── HITL：需要人工时自动弹出对话窗 ─────────────────────────
    /// <summary>完全访问模式：所有需审批的操作自动通过（会话内有效，重启后复位）。</summary>
    public bool FullAccess { get; set; }

    private readonly List<ChatMessage> _queuedInput = new();   // List 支持按条删除/插队
    public event Action<int>? QueueChanged;   // 队列深度变化（UI 显示排队数）

    /// <summary>打断当前回合（停止模型流，已完成部分保留）。</summary>
    public void CancelTurn() => Agent?.CancelTurn();

    /// <summary>排队一条输入：当前回合结束后自动发送（用户在模型工作中补充信息）。支持多模态。</summary>
    public void EnqueueInput(ChatMessage message)
    {
        _queuedInput.Add(message);
        QueueChanged?.Invoke(_queuedInput.Count);
    }

    public string CurrentProvider => Ready ? Agent!.CurrentProvider : LoadProviders(LoadStartupConfig(), UiPrefs.WorkspaceDir()).Active?.Name ?? "";
    public IReadOnlyList<AgentBrook.Agent.Infrastructure.ProviderConfig> Providers => Ready ? Agent!.Providers
        : LoadProviders(LoadStartupConfig(), UiPrefs.WorkspaceDir()).Providers;

    /// <summary>
    /// 启动后台任务：对每个已配置（BaseUrl+Key 非空）供应商拉取模型目录刷新模型列表与类型元数据，
    /// 全部静默执行不阻塞启动；仅在有变化时气泡提示“供应商 + 新增/移除模型或元数据更新”，并持久化保存。
    /// </summary>
    private long _providerRevision;

    private async Task AutoRefreshModelListsAsync()
    {
        try
        {
            if (!Ready) return;
            var agent = Agent!;
            var revision = Interlocked.Read(ref _providerRevision);
            var baseline = agent.Providers.Select(ModelCatalog.Fingerprint).ToList();
            var providers = agent.Providers.Select(ModelCatalog.Clone).ToList();
            var activeProvider = agent.CurrentProvider;
            var changes = new List<string>();
            foreach (var p in providers)
            {
                if (string.IsNullOrWhiteSpace(p.BaseUrl) || string.IsNullOrWhiteSpace(p.ApiKey)) continue;
                var fetched = await FetchModelEntriesAsync(p);
                if (fetched.Count == 0) continue;

                var added = fetched.Select(f => f.Id).Where(id => !p.Models.Contains(id)).ToList();
                var removed = p.Models.Where(m => !fetched.Any(f => f.Id == m)).ToList();
                // 激活模型被下架则回退到首个文本模型（发送前另有拦截兜底）
                if (!ModelCatalog.Apply(p, fetched)) continue;

                var parts = new List<string>();
                if (added.Count > 0) parts.Add("新增 " + string.Join("、", added.Take(5)) + (added.Count > 5 ? $" 等{added.Count}个" : ""));
                if (removed.Count > 0) parts.Add("下架 " + string.Join("、", removed.Take(5)) + (removed.Count > 5 ? $" 等{removed.Count}个" : ""));
                if (parts.Count == 0) parts.Add("模型元数据更新");
                changes.Add($"{p.Name}：{string.Join("；", parts)}");
            }
            if (changes.Count == 0) return;

            await _initializationGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(agent, Agent) || revision != Interlocked.Read(ref _providerRevision)
                    || activeProvider != agent.CurrentProvider
                    || !baseline.SequenceEqual(agent.Providers.Select(ModelCatalog.Fingerprint))) return;
                await agent.SaveProvidersAsync(providers, activeProvider);
                Interlocked.Increment(ref _providerRevision);
            }
            finally
            {
                _initializationGate.Release();
            }
            Dispatcher.UIThread.Post(() => ModelsChanged?.Invoke());
            var line = "🔄 模型列表已自动更新——" + string.Join("；", changes);
            Console.Error.WriteLine($"[models-refresh] {line}");
            // 气泡/对话窗可见提示（与初始化日志同通道）
            EventRaised?.Invoke(new BrookEvent.ToolStarted("log:" + line, null));
            // 气泡弹出提示卡：新增/下架摘要，自动关闭；点击不弹对话窗
            var summary = I18n.T("models.refreshed", string.Join("；", changes));
            Dispatcher.UIThread.Post(() =>
                Bubble?.ShowToast(summary, "#4a9eff", autoClose: TimeSpan.FromSeconds(15)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[models-refresh] failed: {ex.Message}");
        }
    }

    /// <summary>按设置页端点规则拉取供应商模型目录（带缓存穿透参数）；失败返回空列表由调用方静默跳过。</summary>
    private static async Task<List<ModelCatalog.Entry>> FetchModelEntriesAsync(
        AgentBrook.Agent.Infrastructure.ProviderConfig p)
    {
        try
        {
            return await ModelCatalog.FetchAsync(p);
        }
        catch
        {
            return [];
        }
    }

    /// <param name="includeDisabled">true 返回含已停用技能的完整清单（Skill管理界面用）。</param>
    public IReadOnlyList<AgentBrook.Agent.Skills.SkillInfo> GetSkills(bool includeDisabled = false)
        => Ready ? (includeDisabled ? Agent!.SkillStore.All : Agent!.SkillStore.Current) : [];

    public bool DeleteSkill(string name) => Ready && Agent!.DeleteSkill(name);

    /// <summary>启用/停用技能（停用不再预加载、不进入 list_skills/load_skill/主索引，文件保留）。</summary>
    public bool SetSkillEnabled(string name, bool enabled) => Ready && Agent!.SetSkillEnabled(name, enabled);

    public async Task SetProviderAsync(string name)
    {
        Interlocked.Increment(ref _providerRevision);
        await _initializationGate.WaitAsync();
        try
        {
            await Agent!.SwitchProviderAsync(name);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task SaveProvidersAsync(List<AgentBrook.Agent.Infrastructure.ProviderConfig> providers, string activeProvider)
    {
        Interlocked.Increment(ref _providerRevision);
        await _initializationGate.WaitAsync();
        try
        {
            if (Ready)
            {
                await Agent!.SaveProvidersAsync(providers.Select(ModelCatalog.Clone).ToList(), activeProvider);
                return;
            }
            var settings = new ProviderSettings { Providers = providers, ActiveProvider = activeProvider };
            settings.Save(Path.Combine(UiPrefs.WorkspaceDir(), "models.json"));
            await InitializeCoreAsync();
        }
        finally
        {
            _initializationGate.Release();
        }
    }

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

