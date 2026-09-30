using System.ComponentModel;
using System.Text.Json;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.Mcp;
using AgentBrook.Agent.Memory;
using AgentBrook.Agent.Skills;
using AgentBrook.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent;

/// <summary>
/// AgentBrook 通用智能体的可复用核心：组装 MAF ChatClientAgent（模型 + 工具 + 记忆 + 技能 + MCP），
/// 以事件流方式对外提供回合运行，HITL（工具审批 / ask_user 提问）通过 <see cref="IBrookInteraction"/> 回调宿主。
/// 控制台宿主与 Web 客户端（AgentBrook.Helper）共用本核心。
/// </summary>
public sealed class BrookAgent : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly Workspace _workspace;
    private Microsoft.Extensions.AI.IChatClient _chatClient;
    private long _totalIn;
    private long _totalOut;
    private long _totalTotal;
    private readonly IBrookInteraction _interaction;
    private readonly List<McpConnection> _mcpConnections;
    private readonly MarkdownMemoryProvider _memory;
    private readonly IReadOnlyList<SkillInfo> _skills;
    private AIAgent _agent;
    private AgentSession _session;
    private IReadOnlyList<Microsoft.Extensions.AI.AITool> _tools = [];
    private string _skillInstructions = "";
    private ProviderSettings _providers = new();
    private ProviderConfig _activeProvider = new();

    private BrookAgent(
        AppConfig config,
        Workspace workspace,
        Microsoft.Extensions.AI.IChatClient chatClient,
        IBrookInteraction interaction,
        List<McpConnection> mcpConnections,
        MarkdownMemoryProvider memory,
        IReadOnlyList<SkillInfo> skills,
        IReadOnlyList<ToolInfo> toolCatalog,
        AIAgent agent,
        AgentSession session)
    {
        _config = config;
        _workspace = workspace;
        _chatClient = chatClient;
        _interaction = interaction;
        _mcpConnections = mcpConnections;
        _memory = memory;
        _skills = skills;
        _agent = agent;
        _session = session;
        ToolCatalog = toolCatalog;
        CurrentModel = config.LLM.DefaultModel;
    }

    public string Name => _config.Agent.Name;
    public string CurrentModel { get; private set; } = "";
    public string CurrentProvider => _activeProvider.Name;
    public IReadOnlyList<ProviderConfig> Providers => _providers.Providers;
    public IReadOnlyList<string> Models => _activeProvider.Models;
    public string WorkspaceRoot => _workspace.Root;
    public IReadOnlyList<ToolInfo> ToolCatalog { get; }
    public IReadOnlyList<SkillInfo> Skills => _skills;
    public IReadOnlyList<string> ApprovalTools => _config.Agent.RequireApprovalTools;

    /// <summary>创建并初始化智能体（模型客户端、MCP、记忆、技能、会话恢复）。</summary>
    public static async Task<BrookAgent> CreateAsync(
        AppConfig config,
        string? appRootOverride = null,
        IBrookInteraction? interaction = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        interaction ??= new NullInteraction();
        log ??= (_ => { });

        var appRoot = appRootOverride ?? Workspace.LocateAppRoot();
        var workspace = new Workspace(Path.Combine(appRoot, config.Agent.WorkspaceRoot));

        // 模型供应商：优先用户配置（workspace/models.json），否则从 appsettings 派生默认供应商
        var providers = ProviderSettings.Load(workspace.ProvidersFile);
        if (providers.Providers.Count == 0)
        {
            providers.Providers.Add(new ProviderConfig
            {
                Name = "默认",
                BaseUrl = config.LLM.BaseUrl,
                ApiKey = config.LLM.ApiKey,
                Models = [.. config.LLM.Models],
                ActiveModel = config.LLM.DefaultModel,
            });
            providers.ActiveProvider = "默认";
            providers.Save(workspace.ProvidersFile);
        }
        var activeProvider = providers.Active
            ?? throw new InvalidOperationException("模型供应商配置为空：请在模型设置中添加供应商。");
        _ = providers;   // 传递给构造

        IChatClient chatClient = AgentFactory.CreateChatClient(
            activeProvider.BaseUrl, activeProvider.ApiKey,
            string.IsNullOrEmpty(activeProvider.ActiveModel) ? activeProvider.Models.FirstOrDefault() ?? "" : activeProvider.ActiveModel);

        // 合并用户级 MCP 配置（workspace/mcp-user.json，由 mcp_add_server 工具写入）
        var userServers = UserMcpConfigStore.Load(workspace.Root);
        foreach (var server in userServers.Where(u => !config.MCP.Servers.Any(c =>
            string.Equals(c.Name, u.Name, StringComparison.OrdinalIgnoreCase))))
        {
            config.MCP.Servers.Add(server);
        }

        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} mcp connecting...");
        var mcpConnections = await McpToolSource.ConnectAllAsync(config.MCP, workspace, log);
        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} mcp done ({mcpConnections.Count})");

        var memory = new MarkdownMemoryProvider(workspace.MemoryDir, config.Agent.MaxMemoryChars);
        var skills = SkillRegistry.Load(workspace.SkillsDir, workspace.Root);

        var fileTools = new FileTools(workspace, config.Agent.MaxToolOutputChars);
        var shellTools = new ShellTools(workspace, config.Agent.ShellTimeoutSeconds, config.Agent.MaxToolOutputChars);
        var memoryTools = new MemoryTools(workspace, memory);
        var skillStore = new SkillStore(workspace.SkillsDir, workspace.Root);
        var skillTools = new SkillTools(skillStore, config.Agent.SkillMarketUrl);
        var mcpTools = new McpTools(workspace.Root,
            config.MCP.Servers.Select(s => s.Name).ToList());
        var userTools = new UserInteractionTools(interaction);

        // 项目团队：主 Agent 具备派生/指挥工作 Agent 的能力
        var teamTools = new Team.TeamTools(
            workspace.Root,
            (workerName, role, workerInstructions) => AgentFactory.CreateWorkerAgent(
                config, chatClient, Path.Combine(workspace.Root, "projects"),
                workerName, role, workerInstructions),
            notify: msg => log($"🧵 {msg}"));

        var teamInstructions = """

            # 项目团队协作（自我派生）
            接到复杂目标时：先分解为若干角色化子任务 → 用 ask_user 向用户展示派生方案并征得同意 → start_project 开启项目 → spawn_worker 逐个派生（每个会向用户弹审批确认）→ assign_task 分派并收集成果 → 必要时 send_to_worker / broadcast 实时沟通（成员间也可通过各自消息工具协作）→ 全部完成后汇总向用户交付。
            工作产出统一放项目目录：workers/<名字>/（各自独立）与 shared/（共享）。
            """;

        var toolCatalog = new List<ToolInfo>();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(fileTools.list_dir),
            AIFunctionFactory.Create(fileTools.read_file),
            AIFunctionFactory.Create(fileTools.write_file),
            AIFunctionFactory.Create(fileTools.append_file),
            AIFunctionFactory.Create(shellTools.run_command),
            AIFunctionFactory.Create(memoryTools.memory_read),
            AIFunctionFactory.Create(memoryTools.memory_write),
            AIFunctionFactory.Create(memoryTools.memory_append),
            AIFunctionFactory.Create(skillTools.list_skills),
            AIFunctionFactory.Create(skillTools.load_skill),
            AIFunctionFactory.Create(skillTools.create_skill),
            AIFunctionFactory.Create(skillTools.install_skill),
            AIFunctionFactory.Create(skillTools.skill_market),
            AIFunctionFactory.Create(userTools.ask_user),
            // 项目团队（派生与协作）
            AIFunctionFactory.Create(teamTools.start_project),
            AIFunctionFactory.Create(teamTools.spawn_worker),
            AIFunctionFactory.Create(teamTools.assign_task),
            AIFunctionFactory.Create(teamTools.send_to_worker),
            AIFunctionFactory.Create(teamTools.broadcast_to_workers),
            AIFunctionFactory.Create(teamTools.list_workers),
            AIFunctionFactory.Create(teamTools.read_shared),
            // MCP 配置管理
            AIFunctionFactory.Create(mcpTools.mcp_add_server),
            AIFunctionFactory.Create(mcpTools.mcp_list_servers),
        };
        toolCatalog.AddRange(tools.Select(t => new ToolInfo(t.Name, "内置")));
        // MCP 工具黑名单：读取媒体为 base64 的工具会把上下文撑到数十万 token（当前模型无视觉能力）
        string[] mcpToolDenyList = ["read_media_file"];
        foreach (var connection in mcpConnections)
        {
            var allowed = connection.Tools
                .Where(t => !mcpToolDenyList.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var denied in connection.Tools.Where(t => mcpToolDenyList.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"[init] MCP 工具 {denied.Name} 已禁用（媒体读取会挤爆文本上下文）");
            }
            tools.AddRange(allowed);
            toolCatalog.AddRange(allowed.Select(t => new ToolInfo(t.Name, $"MCP:{connection.ServerName}")));
        }

        // HITL：对配置指定的工具包裹审批
        var approvalSet = config.Agent.RequireApprovalTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        tools = [.. tools.Select(t =>
            t is AIFunction f && approvalSet.Contains(t.Name)
                ? new ApprovalRequiredAIFunction(f)
                : t)];;
        var agent = AgentFactory.CreateAgent(config, chatClient, tools, SkillRegistry.BuildSummary(skills) + teamInstructions, memory);
        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} session loading...");
        var session = await LoadOrCreateSessionAsync(agent, workspace.SessionFile, log);
        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} session ok");

        log($"就绪。工具 {tools.Count} 个（内置 {toolCatalog.Count(t => t.Source == "内置")} + MCP {toolCatalog.Count(t => t.Source != "内置")}）｜技能 {skills.Count} 个");
        var brook = new BrookAgent(config, workspace, chatClient, interaction, mcpConnections, memory, skills, toolCatalog, agent, session);
        brook._tools = tools;
        brook._skillInstructions = SkillRegistry.BuildSummary(skills) + teamInstructions;
        brook._providers = providers;
        brook._activeProvider = activeProvider;
        brook.CurrentModel = string.IsNullOrEmpty(activeProvider.ActiveModel)
            ? activeProvider.Models.FirstOrDefault() ?? ""
            : activeProvider.ActiveModel;
        return brook;
    }

    /// <summary>应用累计 Token 消耗。</summary>
    public long TotalInputTokens => _totalIn;
    public long TotalOutputTokens => _totalOut;
    public long TotalTokens => _totalTotal;

    /// <summary>最近一个回合的用量（输入, 输出, 合计）；null 表示该回合未获取到用量。</summary>
    public (long Input, long Output, long Total)? LastTurnUsage { get; private set; }

    /// <summary>最近一个回合的完整回答文本。</summary>
    public string TurnAnswer { get; private set; } = "";

    /// <summary>切换模型（校验配置清单，会话上下文保留）。</summary>
    public void SetModel(string model)
    {
        if (!_activeProvider.Models.Contains(model))
        {
            throw new InvalidOperationException($"未知模型 {model}。可用：{string.Join("、", _activeProvider.Models)}");
        }
        CurrentModel = model;
        _activeProvider.ActiveModel = model;
        _providers.Save(_workspace.ProvidersFile);
    }

    private string _identityName = "";
    private string _language = "zh";
    private string _customPrompt = "";

    /// <summary>
    /// 应用助手个性化设置（名字 / 交流语言 / 默认提示词）：改写系统指令后热重建智能体，会话上下文保留。
    /// </summary>
    public async Task ApplyAssistantSettingsAsync(string name, string language, string customPrompt)
    {
        var newName = (name ?? "").Trim();
        var newLang = language == "en" ? "en" : "zh";
        var newPrompt = (customPrompt ?? "").Trim();
        if (newName == _identityName && newLang == _language && newPrompt == _customPrompt)
        {
            return;
        }

        var instructions = _config.Agent.Instructions;

        // 1) 名字：只替换身份句（避免误伤 AgentBrook3.0 等产品词）
        if (newName.Length > 0)
        {
            if (_identityName.Length > 0)
            {
                instructions = instructions.Replace($"你是 {_identityName}，", $"你是 {newName}，");
            }
            else
            {
                instructions = System.Text.RegularExpressions.Regex.Replace(
                    instructions, @"你是\s*Brook\s*，", $"你是 {newName}，");
            }
            if (instructions.Contains("Brook，"))
            {
                instructions = instructions.Replace("Brook，", $"{newName}，");
            }
            _config.Agent.Name = newName.ToLowerInvariant();
            _identityName = newName;
        }

        // 2) 交流语言
        instructions = newLang == "en"
            ? instructions.Replace("用中文与用户交流", "用英文与用户交流")
            : instructions.Replace("用英文与用户交流", "用中文与用户交流");
        _language = newLang;

        // 3) 默认提示词：移除旧段、追加新段
        if (_customPrompt.Length > 0)
        {
            instructions = instructions.Replace("\n\n# 用户自定义指令\n" + _customPrompt, "");
        }
        if (newPrompt.Length > 0)
        {
            instructions += "\n\n# 用户自定义指令\n" + newPrompt;
        }
        _customPrompt = newPrompt;
        _config.Agent.Instructions = instructions;

        // 4) 重建智能体（instructions 变化），会话上下文保留
        System.Text.Json.JsonElement? carried = null;
        try
        {
            carried = await _agent.SerializeSessionAsync(_session);
        }
        catch { }
        _agent = AgentFactory.CreateAgent(_config, _chatClient, _tools, _skillInstructions, _memory);
        if (carried is not null)
        {
            try
            {
                _session = await _agent.DeserializeSessionAsync(carried.Value);
            }
            catch
            {
                _session = await _agent.CreateSessionAsync();
            }
        }
        else
        {
            _session = await _agent.CreateSessionAsync();
        }
    }

    /// <summary>切换供应商：重建模型客户端与智能体，会话上下文迁移。</summary>
    public async Task SwitchProviderAsync(string providerName)
    {
        var target = _providers.Providers.FirstOrDefault(p => p.Name == providerName)
            ?? throw new InvalidOperationException($"未知供应商 {providerName}");
        if (!target.IsConfigured)
        {
            throw new InvalidOperationException($"供应商 {providerName} 尚未配置完整（需要 BaseUrl、ApiKey 和至少一个模型）。");
        }
        if (target.Name == _activeProvider.Name)
        {
            return;
        }
        System.Text.Json.JsonElement? carried = null;
        try
        {
            carried = await _agent.SerializeSessionAsync(_session);
        }
        catch { }
        ApplyProvider(target);
        if (carried is not null)
        {
            try
            {
                _session = await _agent.DeserializeSessionAsync(carried.Value);
                return;
            }
            catch
            {
                // 跨供应商会话迁移失败时退回新会话（历史仍保留在原供应商存档中）
            }
        }
        _session = await _agent.CreateSessionAsync();
    }

    /// <summary>保存供应商配置并热应用；激活供应商变化时重建客户端并迁移会话。</summary>
    public async Task SaveProvidersAsync(List<ProviderConfig> providers, string activeProvider)
    {
        _providers.Providers = providers;
        _providers.ActiveProvider = activeProvider;
        _providers.Save(_workspace.ProvidersFile);
        var target = _providers.Active
            ?? throw new InvalidOperationException("至少需要保留一个供应商。");
        if (!target.IsConfigured)
        {
            throw new InvalidOperationException($"供应商 {target.Name} 配置不完整（BaseUrl / ApiKey / 模型清单）。");
        }

        var switched = target.Name != _activeProvider.Name
            || target.BaseUrl != _activeProvider.BaseUrl
            || target.ApiKey != _activeProvider.ApiKey;
        if (switched)
        {
            System.Text.Json.JsonElement? carried = null;
            try
            {
                carried = await _agent.SerializeSessionAsync(_session);
            }
            catch { }
            ApplyProvider(target);
            if (carried is not null)
            {
                try
                {
                    _session = await _agent.DeserializeSessionAsync(carried.Value);
                }
                catch
                {
                    _session = await _agent.CreateSessionAsync();
                }
            }
            else
            {
                _session = await _agent.CreateSessionAsync();
            }
        }
        else
        {
            _activeProvider = target;
            if (!target.Models.Contains(CurrentModel, StringComparer.Ordinal))
            {
                CurrentModel = target.ActiveModel is { Length: > 0 } ? target.ActiveModel : target.Models.FirstOrDefault() ?? "";
            }
            // 同供应商细节变化（key 轮换/模型清单调整）也需要重建客户端
            if (target.BaseUrl != _activeProvider.BaseUrl || target.ApiKey != _activeProvider.ApiKey)
            {
                ApplyProvider(target);
            }
        }
    }

    private void ApplyProvider(ProviderConfig target)
    {
        _activeProvider = target;
        _chatClient = AgentFactory.CreateChatClient(
            target.BaseUrl, target.ApiKey,
            string.IsNullOrEmpty(target.ActiveModel) ? target.Models.FirstOrDefault() ?? "" : target.ActiveModel);
        _agent = AgentFactory.CreateAgent(_config, _chatClient, _tools, _skillInstructions, _memory);
        CurrentModel = string.IsNullOrEmpty(target.ActiveModel)
            ? target.Models.FirstOrDefault() ?? ""
            : target.ActiveModel;
        _providers.ActiveProvider = target.Name;
        _providers.Save(_workspace.ProvidersFile);
    }

    /// <summary>当前长期记忆内容（调试/展示用）。</summary>
    public string LoadMemory() => _memory.LoadAllMemory();

    public IReadOnlyList<string> MemoryFiles => _memory.ListFiles();

    /// <summary>开启新会话（清空对话历史，长期记忆不受影响）。</summary>
    public async Task NewSessionAsync()
    {
        _session = await _agent.CreateSessionAsync();
        await SaveSessionAsync();
    }

    public Task SaveSessionAsync() => SaveSessionCoreAsync();

    /// <summary>
    /// 运行一个用户回合：流式产出事件；若触发 HITL（审批）则回调
    /// <see cref="IBrookInteraction"/> 征询决定并续跑，循环直到回合完结。
    /// ask_user 的提问由工具内部回调 <see cref="IBrookInteraction.AskUserAsync"/>。
    /// </summary>
    public async IAsyncEnumerable<BrookEvent> RunAsync(
        string userText,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage>? followUp = null;
        TurnAnswer = "";
        SanitizeOrphanApprovals(_session);   // 上回合中断可能留下孤儿审批请求，先清理

        yield return new BrookEvent.Status(BrookRunState.Thinking);

        while (true)
        {
            var approvals = new List<ToolApprovalRequestContent>();
            var roundUpdates = new List<Microsoft.Agents.AI.AgentResponseUpdate>();

            var updates = followUp is null
                ? _agent.RunStreamingAsync(userText, _session, BuildRunOptions())
                : _agent.RunStreamingAsync(followUp, _session, BuildRunOptions());

            // 每轮 stream 重置：TurnAnswer 只保留最后一轮（最终回答），不累积中间轮的过渡文本
            TurnAnswer = "";

            await foreach (var update in updates.WithCancellation(cancellationToken))
            {
                roundUpdates.Add(update);
                if (!string.IsNullOrEmpty(update.Text))
                {
                    TurnAnswer += update.Text;
                }
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent call)
                    {
                        var argsJson = call.Arguments is { Count: > 0 } args
                            ? JsonSerializer.Serialize(args)
                            : null;
                        yield return new BrookEvent.ToolStarted(call.Name, argsJson);
                        if (call.Name != "ask_user")
                        {
                            yield return new BrookEvent.Status(BrookRunState.CallingTool, call.Name);
                        }
                    }
                    else if (content is FunctionResultContent result)
                    {
                        // 执行反馈：把工具结果预览透出给宿主
                        var preview = result.Exception is not null
                            ? "错误：" + result.Exception.Message
                            : TruncateForPreview(result.Result?.ToString());
                        yield return new BrookEvent.ToolCompleted(result.CallId, preview);
                    }
                    if (content is ToolApprovalRequestContent approval)
                    {
                        approvals.Add(approval);
                    }
                }
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return new BrookEvent.TextDelta(update.Text);
                }
            }

            // 聚合本轮（该轮 stream）的 Token 用量并累计
            if (roundUpdates.Count > 0)
            {
                var aggregated = Microsoft.Agents.AI.AgentResponseExtensions.ToAgentResponse(roundUpdates);
                var usage = aggregated.Usage;
                if (usage is not null)
                {
                    var input = usage.InputTokenCount ?? 0;
                    var output = usage.OutputTokenCount ?? 0;
                    var total = usage.TotalTokenCount ?? input + output;
                    _totalIn += input;
                    _totalOut += output;
                    _totalTotal += total;
                    LastTurnUsage = (input, output, total);
                    yield return new BrookEvent.Usage(input, output, total, _totalTotal);
                }
            }

            if (approvals.Count == 0)
            {
                break;
            }

            followUp = [];
            foreach (var request in approvals)
            {
                var call = request.ToolCall as FunctionCallContent;
                var argsJson = call?.Arguments is { Count: > 0 } args
                    ? JsonSerializer.Serialize(args)
                    : null;

                yield return new BrookEvent.Status(BrookRunState.AwaitingApproval, call?.Name);
                var decision = await _interaction.GetApprovalAsync(call?.Name ?? request.ToolCall.CallId, argsJson, cancellationToken);

                followUp.Add(new ChatMessage(ChatRole.User,
                    [request.CreateResponse(decision.Approved, decision.Reason)]));
                yield return new BrookEvent.Status(BrookRunState.Thinking);
            }

            await SaveSessionCoreAsync();
        }

        await SaveSessionCoreAsync();
        yield return new BrookEvent.TurnCompleted();
        yield return new BrookEvent.Status(BrookRunState.Idle);
    }

    private static string? TruncateForPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private ChatClientAgentRunOptions BuildRunOptions() =>
        new(new ChatOptions { ModelId = CurrentModel });

    /// <summary>
    /// 清理会话历史里没有对应响应的审批请求。
    /// 回合在审批等待/流式中途被打断时，请求已入历史但响应尚未生成；
    /// 残留会让 MAF 在续跑时抛 "no matching ToolApprovalResponseContent"。
    /// 处理方式：直接移除孤儿请求（模型只会少看到一个被中断的调用）。
    /// </summary>
    private static void SanitizeOrphanApprovals(AgentSession session)
    {
        try
        {
            if (!session.TryGetInMemoryChatHistory(out var history, null, null) || history is null)
            {
                return;
            }
            var answered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var msg in history)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is ToolApprovalResponseContent { ToolCall.CallId: { } rid } && !string.IsNullOrEmpty(rid))
                    {
                        answered.Add(rid);
                    }
                }
            }
            var removed = 0;
            foreach (var msg in history)
            {
                for (var i = msg.Contents.Count - 1; i >= 0; i--)
                {
                    if (msg.Contents[i] is ToolApprovalRequestContent { ToolCall.CallId: { } cid }
                        && !string.IsNullOrEmpty(cid)
                        && !answered.Contains(cid))
                    {
                        msg.Contents.RemoveAt(i);
                        removed++;
                    }
                }
            }
            if (removed > 0)
            {
                session.SetInMemoryChatHistory(history, null, null);
            }
        }
        catch
        {
            // 清理失败不阻塞主流程：失败时 MAF 会给出原始错误，可再行排查
        }
    }

    private async Task SaveSessionCoreAsync()
    {
        try
        {
            SanitizeOrphanApprovals(_session);
            var serialized = await _agent.SerializeSessionAsync(_session);
            File.WriteAllText(_workspace.SessionFile, serialized.GetRawText());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"会话保存失败：{ex.Message}", ex);
        }
    }

    private static async Task<AgentSession> LoadOrCreateSessionAsync(AIAgent agent, string sessionFile, Action<string> log)
    {
        if (File.Exists(sessionFile))
        {
            try
            {
                var json = JsonDocument.Parse(File.ReadAllText(sessionFile)).RootElement;
                log("已从存档恢复上次会话。");
                var restored = await agent.DeserializeSessionAsync(json);
                SanitizeOrphanApprovals(restored);
                return restored;
            }
            catch (Exception ex)
            {
                log($"会话存档无法恢复（{ex.Message}），已开启新会话。");
            }
        }
        return await agent.CreateSessionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _mcpConnections)
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>无宿主交互时的默认实现：拒绝审批、回答“未接入交互”。仅用于测试。</summary>
    private sealed class NullInteraction : IBrookInteraction
    {
        public Task<ApprovalDecision> GetApprovalAsync(string toolName, string? argumentsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApprovalDecision.Reject("当前运行模式未接入交互界面"));

        public Task<string> AskUserAsync(string question, string? choices, CancellationToken cancellationToken = default) =>
            Task.FromResult("（当前运行模式未接入交互界面，请以最合理的方式自行继续）");
    }
}
