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
    private CancellationTokenSource? _turnCts;
    private IReadOnlyList<Microsoft.Extensions.AI.AITool> _tools = [];
    private string _skillInstructions = "";
    private ProviderSettings _providers = new();
    private ProviderConfig _activeProvider = new();
    private Infrastructure.ModelRouter _router = null!;
    private string _historySummary = "";   // 滚动摘要：被压缩掉的早期对话的浓缩版，注入历史头部保证连续性
    private Infrastructure.SessionStore _sessions = null!;
    private Action? _refreshMasterIndex;   // 能力变化（如会话沉淀为技能）后重建主索引
    private Skills.SkillStore _skillStore = null!;
    private string _sessionId = "";
    private string _sessionFile = "";
    private string _sessionSummaryFile = "";
    private bool _titleAutoSet;   // 本会话标题是否已从首条用户消息自动生成

    /// <summary>会话标题自动生成/切换后触发（UI 刷新顶栏标题）。</summary>
    public event Action<string>? SessionTitleChanged;

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

    /// <summary>
    /// 回合前置校验：活跃供应商的 Key 是否可用于发请求。
    /// 占位符 Key（含中文的"请填写"类）会导致 HTTP 头非 ASCII 的隐晦错误，这里转成可操作的明确提示。
    /// 返回 null 表示可用，否则为错误说明。
    /// </summary>
    public string? ValidateActiveProvider()
    {
        var p = _providers.Active;
        if (p is null)
        {
            return "模型供应商配置为空：请在模型设置中添加供应商。";
        }
        var key = p.ApiKey ?? "";
        Console.Error.WriteLine($"[validate] provider「{p.Name}」BaseUrl={p.BaseUrl} key 前 6 位={key[..Math.Min(6, key.Length)]}… 长度={key.Length} 非ASCII={key.Any(c => c > 127)}");
        if (key.Any(c => c > 127))
        {
            return "API Key 含非 ASCII 字符（可能是占位符）：请在模型设置中填写你的真实 Key。";
        }
        if (key.Contains("请", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("please-fill", StringComparison.OrdinalIgnoreCase))
        {
            return "尚未填写 API Key：请在模型设置中填入你的 Key。";
        }
        if (string.IsNullOrWhiteSpace(p.BaseUrl))
        {
            return "模型 BaseUrl 未配置：请在模型设置中填写。";
        }
        return null;
    }
    public IReadOnlyList<string> Models => _activeProvider.Models;
    public string WorkspaceRoot => _workspace.Root;
    public IReadOnlyList<ToolInfo> ToolCatalog { get; }
    public IReadOnlyList<SkillInfo> Skills => _skills;
    public SkillStore SkillStore => _skillStore;
    public IReadOnlyList<string> ApprovalTools => _config.Agent.RequireApprovalTools;

    /// <summary>删除已安装技能并重建主能力索引。</summary>
    public bool DeleteSkill(string name)
    {
        if (_skillStore.DeleteSkill(name))
        {
            _refreshMasterIndex?.Invoke();
            return true;
        }
        return false;
    }

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

        // 模型路由：子Agent/摘要等辅助角色用清单中的轻量模型（分级用模，降本提速）
        var router = new ModelRouter(config, () => providers.Active ?? activeProvider);

        var skills = SkillRegistry.Load(workspace.SkillsDir, workspace.Root);

        // 子Agent 委派（客户端延迟解析：经由 brook 实例，供应商切换后自动跟随新路由）
        BrookAgent? brookRef = null;
        Action? brookRef2SetMasterIndex = null;
        var subAgentTools = new Tools.SubAgentTools(
            workspace.Root,
            () => brookRef!._router.Resolve(ModelRouter.RoleSubAgent, brookRef!._chatClient));

        var fileTools = new FileTools(workspace, config.Agent.MaxToolOutputChars);
        var shellTools = new ShellTools(workspace, config.Agent.ShellTimeoutSeconds, config.Agent.MaxToolOutputChars);
        var memoryTools = new MemoryTools(workspace, memory);
        var skillStore = new SkillStore(workspace.SkillsDir, workspace.Root);

        // 能力变化（创建/安装技能）后重建主索引（mcpGate 稍后赋值，闭包晚绑定）
        Mcp.McpGateTools? mcpGateRef = null;
        void RefreshMasterIndex()
        {
            var fresh = SkillRegistry.Load(workspace.SkillsDir, workspace.Root);
            MasterIndex.Rebuild(workspace.SkillsDir, fresh, mcpGateRef, BuiltinToolOverview);
        }
        var skillTools = new SkillTools(skillStore, config.Agent.SkillMarketUrl,
            onCapabilitiesChanged: reason => RefreshMasterIndex(),
            sessionInfoProvider: () => (brookRef!._historySummary, brookRef!.TurnAnswer));
        brookRef2SetMasterIndex = () => RefreshMasterIndex();   // 供会话沉淀等非工具路径复用
        var mcpTools = new McpTools(workspace.Root,
            config.MCP.Servers.Select(s => s.Name).ToList());
        var userTools = new UserInteractionTools(interaction);

        // 项目团队：主 Agent 具备派生/指挥工作 Agent 的能力
        var teamTools = new Team.TeamTools(
            workspace.Root,
            config,
            interaction,
            (projectRoot, workerName, role, workerInstructions, caps) => AgentFactory.CreateWorkerAgent(
                config, chatClient, projectRoot, workerName, role, workerInstructions,
                mcpConnections, caps),
            notify: msg => log($"🧵 {msg}"));

        var teamInstructions = """

            # 项目团队协作（自我派生）
            接到复杂目标时：先分解为若干角色化子任务 → 用 ask_user 向用户展示派生方案并征得同意 → start_project 开启项目 → spawn_worker 逐个派生（每个会向用户弹审批确认）→ assign_task 分派并收集成果 → 必要时 send_to_worker / broadcast 实时沟通（成员间也可通过各自消息工具协作）→ 全部完成后汇总向用户交付。
            工作产出统一放项目目录：workers/<名字>/（各自独立）与 shared/（共享）。
            """;

        var delegateInstructions = """

            # 子Agent 委派（上下文隔离）
            复杂子任务（文献调研、多文件梳理、长报告、批量整理）用 delegate_task 委派子Agent独立完成：中间工具输出隔离在子Agent上下文，只有最终结论回到本对话。委派的任务描述必须自包含。简单任务（一两次工具调用）直接自己做，不要委派。
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
            // 子Agent 委派（轻量，无需审批：子Agent 只有工作区文件工具）
            AIFunctionFactory.Create(subAgentTools.delegate_task),
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

        // MCP 按需调度（渐进披露）：schema 不进系统提示词，主索引只列工具名+一句话用途，
        // 模型按需 mcp_tool_help 查参数、mcp_call 调用——固定 token 成本大幅下降
        var mcpGate = new Mcp.McpGateTools(mcpConnections, config.Agent.MaxToolOutputChars);
        mcpGateRef = mcpGate;
        var mcpCount = mcpGate.ToolCount;
        foreach (var connection in mcpConnections)
        {
            toolCatalog.AddRange(connection.Tools.Select(t => new ToolInfo(t.Name, $"MCP:{connection.ServerName}")));
        }
        tools.Add(AIFunctionFactory.Create(mcpGate.mcp_call));
        tools.Add(AIFunctionFactory.Create(mcpGate.mcp_tool_help));
        toolCatalog.Add(new ToolInfo("mcp_call", "内置"));
        toolCatalog.Add(new ToolInfo("mcp_tool_help", "内置"));

        // 主能力索引：整合技能+MCP+内置工具为自动更新的主技能（能力变化后由对应工具触发重建）
        MasterIndex.Rebuild(workspace.SkillsDir, skills, mcpGate, BuiltinToolOverview);
        skills = SkillRegistry.Load(workspace.SkillsDir, workspace.Root);   // 重载以纳入 _master

        // HITL：对配置指定的工具包裹审批
        var approvalSet = config.Agent.RequireApprovalTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        tools = [.. tools.Select(t =>
            t is AIFunction f && approvalSet.Contains(t.Name)
                ? new ApprovalRequiredAIFunction(f)
                : t)];;
        var capabilityBlock = SkillRegistry.BuildSummary(skills) + "\n\n" + mcpGate.BuildCatalogBlock() + delegateInstructions + teamInstructions;
        var agent = AgentFactory.CreateAgent(config, chatClient, tools, capabilityBlock, memory);

        // 启动即报告提示词规模（上下文效率度量：系统指令 + 工具 schema 字符数）
        var instrChars = (config.Agent.Instructions.Length + capabilityBlock.Length);
        var schemaChars = tools.OfType<AIFunction>().Sum(t => SafeSchemaLength(t));
        Console.Error.WriteLine(
            $"[init] 提示词规模：指令 {instrChars:N0} 字符（含索引/团队/委派）｜工具 schema {schemaChars:N0} 字符 × {tools.Count(t => t is AIFunction)} 个工具");

        // 多会话存储：自动迁移旧版单会话文件；以最近使用的会话为当前
        var sessions = new Infrastructure.SessionStore(
            workspace.SessionsDir, workspace.SessionFile, workspace.SessionSummaryFile);
        var current = sessions.Listed.FirstOrDefault() ?? sessions.Create("新对话");

        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} session loading...");
        var session = await LoadOrCreateSessionAsync(agent, sessions.SessionFile(current.Id), log);
        Console.Error.WriteLine($"[init] {DateTime.Now:HH:mm:ss.fff} session ok");

        log(CoreStrings.L(
            $"就绪。直接可用工具 {tools.Count} 个，MCP {mcpCount} 个已按需调度（mcp_call 调用）｜技能 {skills.Count} 个",
            $"Ready. {tools.Count} direct tools, {mcpCount} MCP tools gated on demand (mcp_call) | skills {skills.Count}"));
        var brook = new BrookAgent(config, workspace, chatClient, interaction, mcpConnections, memory, skills, toolCatalog, agent, session);
        brookRef = brook;
        brook._tools = tools;
        brook._skillInstructions = capabilityBlock;
        brook._providers = providers;
        brook._activeProvider = activeProvider;
        brook._router = router!;
        brook._refreshMasterIndex = brookRef2SetMasterIndex;
        brook._sessions = sessions;
        brook._skillStore = skillStore;
        brook._sessionId = current.Id;
        brook._sessionFile = sessions.SessionFile(current.Id);
        brook._sessionSummaryFile = sessions.SummaryFile(current.Id);
        brook._titleAutoSet = current.Title != "新对话" && current.Title != "上次会话";
        brook._historySummary = File.Exists(brook._sessionSummaryFile)
            ? File.ReadAllText(brook._sessionSummaryFile) : "";
        brook.CurrentModel = string.IsNullOrEmpty(activeProvider.ActiveModel)
            ? activeProvider.Models.FirstOrDefault() ?? ""
            : activeProvider.ActiveModel;
        return brook;
    }

    /// <summary>内置工具分组概览（主索引用）。</summary>
    private static string BuiltinToolOverview =>
        "- 文件：list_dir / read_file / write_file / append_file（工作区沙箱）\n" +
        "- 命令：run_command（shell，需审批）\n" +
        "- 记忆：memory_read / memory_write / memory_append\n" +
        "- 技能：list_skills / load_skill / create_skill / install_skill / skill_market\n" +
        "- 交互：ask_user；委派：delegate_task；团队：start_project / spawn_worker / assign_task 等；MCP 管理：mcp_add_server / mcp_list_servers；MCP 调度：mcp_call / mcp_tool_help";

    private static long SafeSchemaLength(AIFunction f)
    {
        try { return f.JsonSchema.ValueKind is System.Text.Json.JsonValueKind.Object ? f.JsonSchema.GetRawText().Length : 0; }
        catch { return 0; }
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

        // 1) 名字：身份句先规范化（无论之前是什么名字/变形，归一为「你是 X，」）再放入新名，
        //    避免状态残留导致的双重替换（如 HiHiBrook）
        if (newName.Length > 0)
        {
            instructions = System.Text.RegularExpressions.Regex.Replace(
                instructions, @"你是\s*\S{1,40}，", "你是 ‹NAME›，");
            instructions = instructions.Replace("你是 ‹NAME›，", $"你是 {newName}，");
            _config.Agent.Name = newName.ToLowerInvariant();
            _identityName = newName;
        }

        // 2) 交流语言：整句强制替换（清除任何历史变形，且不留"除非用户…"的跟随漏洞）
        var langRule = newLang == "en"
            ? "5. Always reply in English, regardless of the language the user writes in. Do not mirror the user's language."
            : "5. 始终用中文回复，无论用户使用什么语言书写，都不要跟随用户的语言。";
        instructions = System.Text.RegularExpressions.Regex.Replace(
            instructions,
            @"5\.\s*用(中文|英文)与用户交流（除非用户使用其他语言）。|5\.\s*Always reply in English[^\n]*。",
            langRule);
        _language = newLang;
        _config.Agent.ReplyLanguage = newLang;   // 供 AgentFactory 在重建时注入末尾语言强指令

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
        var head = instructions[..Math.Min(80, instructions.Length)].Replace("\n", " ");
        System.Console.Error.WriteLine(
            $"[apply-core] name={_identityName} lang={_language} prompt={_customPrompt} " +
            $"instrEn={instructions.Contains("Always reply in English")} instrZh={instructions.Contains("始终用中文回复")} " +
            $"instrName={instructions.Contains($"你是 {newName}，")} customSeg={instructions.Contains("# 用户自定义指令")} " +
            $"head=[{head}]");

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

    /// <summary>打断当前回合：取消模型流与审批等待，已完成部分保留在会话中。</summary>
    public void CancelTurn()
    {
        _turnCts?.Cancel();
    }

    /// <summary>本回合是否已被用户请求打断（区分用户停止与外部取消）。</summary>
    public bool TurnCancelRequested => _turnCts is { IsCancellationRequested: true };

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
        _router.Reset();   // 辅助客户端按旧供应商缓存，全部失效重建
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

    /// <summary>开启新会话：当前会话归档进会话列表（不丢历史），切换到全新会话。</summary>
    public async Task NewSessionAsync()
    {
        try
        {
            await SaveSessionCoreAsync();      // 旧会话落盘归档；失败不阻断新建
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[session] 新建前保存当前会话失败（忽略继续）：{ex.Message}");
        }
        var meta = _sessions.Create("新对话");
        _sessionId = meta.Id;
        _sessionFile = _sessions.SessionFile(_sessionId);
        _sessionSummaryFile = _sessions.SummaryFile(_sessionId);
        _historySummary = "";
        _titleAutoSet = false;
        _session = await _agent.CreateSessionAsync();
        await SaveSessionCoreAsync();
        SessionTitleChanged?.Invoke(meta.Title);
    }

    /// <summary>
    /// 导出当前会话的可读对话记录（user/assistant 文本对，跳过工具中间轮），
    /// 供 UI 切换会话后回放历史。最多返回最近 limit 条。
    /// </summary>
    public IReadOnlyList<(string Role, string Text)> GetTranscript(int limit = 40)
    {
        var list = new List<(string, string)>();
        try
        {
            if (!_session.TryGetInMemoryChatHistory(out var history, null, null) || history is null)
            {
                return list;
            }
            foreach (var m in history)
            {
                if (m.Role == ChatRole.System) continue;   // 跳过前情摘要
                var text = (m.Text ?? "").Trim();
                if (text.Length == 0) continue;            // 工具调用/结果轮无正文
                if (m.Role == ChatRole.User) list.Add(("user", text));
                else if (m.Role == ChatRole.Assistant) list.Add(("assistant", text));
            }
        }
        catch { }
        return list.Skip(Math.Max(0, list.Count - limit)).ToList();
    }

    /// <summary>当前会话 ID 与标题。</summary>
    public string CurrentSessionId => _sessionId;
    public string CurrentSessionTitle => _sessions.Get(_sessionId)?.Title ?? "新对话";
    public IReadOnlyList<SessionMeta> Sessions => _sessions.Listed;

    /// <summary>切换会话：当前会话落盘后载入目标会话（含各自的前情摘要），上下文完整跟随。</summary>
    public async Task<bool> SwitchSessionAsync(string id)
    {
        var meta = _sessions.Get(id);
        if (meta is null || id == _sessionId) return id == _sessionId;
        try
        {
            await SaveSessionCoreAsync();      // 当前会话落盘；失败不阻断切换
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[session] 切换前保存当前会话失败（忽略继续切换）：{ex.Message}");
        }
        _sessionId = id;
        _sessionFile = _sessions.SessionFile(id);
        _sessionSummaryFile = _sessions.SummaryFile(id);
        _titleAutoSet = meta.Title != "新对话";
        _historySummary = File.Exists(_sessionSummaryFile)
            ? File.ReadAllText(_sessionSummaryFile) : "";
        try
        {
            if (File.Exists(_sessionFile))
            {
                var json = JsonDocument.Parse(File.ReadAllText(_sessionFile)).RootElement;
                _session = await _agent.DeserializeSessionAsync(json);
            }
            else
            {
                _session = await _agent.CreateSessionAsync();
            }
        }
        catch
        {
            _session = await _agent.CreateSessionAsync();
        }
        SessionTitleChanged?.Invoke(meta.Title);
        return true;
    }

    public void RenameSession(string id, string title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return;
        _sessions.SetTitle(id, t);
        if (id == _sessionId) SessionTitleChanged?.Invoke(t);
    }

    /// <summary>删除会话；删的是当前会话时自动切到最近的会话（无则新建）。</summary>
    public async Task DeleteSessionAsync(string id)
    {
        _sessions.Remove(id);
        if (id != _sessionId) return;
        var next = _sessions.Listed.FirstOrDefault();
        if (next is null)
        {
            await NewSessionAsync();
        }
        else
        {
            await SwitchSessionAsync(next.Id);
        }
    }

    /// <summary>
    /// 把当前会话成果沉淀为技能（skills/session-&lt;slug&gt;/SKILL.md）：
    /// 成果要点 + 滚动摘要 + 末次回答尾部，其他会话 load_skill 即可调用。
    /// 返回（技能名, 错误）。
    /// </summary>
    public (string Name, string? Error) DistillSessionToSkill(string? slugHint = null)
    {
        var baseTitle = CurrentSessionTitle == "新对话" ? "会话成果" : CurrentSessionTitle;
        var slug = Slugify(slugHint is { Length: > 0 } ? slugHint : baseTitle);
        var name = $"session-{slug}";
        var desc = $"会话成果：{baseTitle}。需要复用该会话的结论、产出文件或上下文时加载。";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 会话成果：{baseTitle}（沉淀于 {DateTime.Now:yyyy-MM-dd HH:mm}）");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(_historySummary))
        {
            sb.AppendLine("## 会话脉络（滚动摘要）");
            sb.AppendLine(_historySummary);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(TurnAnswer))
        {
            var tail = TurnAnswer.Length > 3000 ? TurnAnswer[^3000..] : TurnAnswer;
            sb.AppendLine("## 末次交付（结尾部分）");
            sb.AppendLine(tail);
        }
        var content = sb.ToString();
        var error = _skillStore.CreateSkill(name, desc, content, out _);
        if (error is null)
        {
            _refreshMasterIndex?.Invoke();   // 主索引同步纳入新成果技能
        }
        return (name, error);
    }

    private static string Slugify(string text)
    {
        var raw = text.Trim().ToLowerInvariant();
        var hasCjk = raw.Any(c => c >= 0x4e00 && c <= 0x9fff);
        if (hasCjk)
        {
            // 中文标题：取时间戳短码，避免空 slug
            return $"{DateTime.Now:MMdd-HHmmss}";
        }
        var slug = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray()).Trim('-');
        return slug.Length >= 2 ? slug[..Math.Min(slug.Length, 40)] : $"s{DateTime.Now:MMdd-HHmmss}";
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
        await foreach (var e in RunAsync(new ChatMessage(ChatRole.User, userText), cancellationToken))
        {
            yield return e;
        }
    }

    /// <summary>
    /// 运行一个用户回合（多模态入口）：支持 TextContent / DataContent 等 MEAI 内容。
    /// </summary>
    public async IAsyncEnumerable<BrookEvent> RunAsync(
        ChatMessage userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage>? followUp = null;
        TurnAnswer = "";
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _turnCts.Token);
        var ct = linked.Token;

        // 首条用户消息自动生成会话标题（一次性）
        var messageText = userMessage.Text ?? "";
        if (!_titleAutoSet && !string.IsNullOrWhiteSpace(messageText))
        {
            var title = messageText.Trim().Replace("\n", " ");
            if (title.Length > 18) title = title[..18] + "…";
            _sessions.SetTitle(_sessionId, title);
            _titleAutoSet = true;
            SessionTitleChanged?.Invoke(title);
        }

        SanitizeOrphanApprovals(_session);   // 上回合中断可能留下孤儿审批请求，先清理
        var compacted = await AutoCompactContextAsync(ct); // 历史超阈值时自动压缩：早期对话转前情摘要，防上下文溢出
        if (compacted.RemovedMessages > 0)
        {
            yield return new BrookEvent.ToolStarted(
                compacted.Summarized
                    ? "log:🧹 已自动压缩上下文：早期 " + compacted.RemovedMessages + " 条消息浓缩为前情摘要" +
                      $"（估算 {compacted.EstimatedTokensBefore / 1000}K → {compacted.EstimatedTokensAfter / 1000}K tokens），会话连续性保留"
                    : "log:🧹 已自动压缩上下文：移除最早 " + compacted.RemovedMessages + " 条消息" +
                      $"（估算 {compacted.EstimatedTokensBefore / 1000}K → {compacted.EstimatedTokensAfter / 1000}K tokens），最近对话保留", null);
        }

        yield return new BrookEvent.Status(BrookRunState.Thinking);

        while (true)
        {
            await AutoCompactContextAsync(ct);   // 审批续跑等多轮流程中每轮都保持预算内
            var approvals = new List<ToolApprovalRequestContent>();
            var roundUpdates = new List<Microsoft.Agents.AI.AgentResponseUpdate>();

            var updates = followUp is null
                ? _agent.RunStreamingAsync([userMessage], _session, BuildRunOptions())
                : _agent.RunStreamingAsync(followUp, _session, BuildRunOptions());

            // 每轮 stream 重置：TurnAnswer 只保留最后一轮（最终回答），不累积中间轮的过渡文本
            TurnAnswer = "";

            // callId → 工具名：ToolCompleted 事件需要真实工具名（FunctionResultContent 只带 CallId），
            // 供宿主做"哪个工具完成"的精确匹配（如气泡轨道图标的成对显隐）。
            var callNames = new Dictionary<string, string>();

            await foreach (var update in updates.WithCancellation(ct))
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
                        callNames[call.CallId] = call.Name;
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
                        var completedName = callNames.GetValueOrDefault(result.CallId, result.CallId);
                        yield return new BrookEvent.ToolCompleted(completedName, preview);
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
                var decision = await _interaction.GetApprovalAsync(call?.Name ?? request.ToolCall.CallId, argsJson, ct);

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

    /// <summary>
    /// 轻量一次性补全（走摘要档模型，不进会话历史）：启动问候语等旁路生成用。
    /// 失败由调用方兜底（问候语等场景静默降级）。
    /// </summary>
    public async Task<string> QuickCompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var (_, client) = _router.Resolve(ModelRouter.RoleSummarize, _chatClient);
        var response = await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt),
        ], cancellationToken: cancellationToken);
        return (response.Text ?? "").Trim();
    }

    private ChatClientAgentRunOptions BuildRunOptions() =>
        new(new ChatOptions { ModelId = CurrentModel });

    /// <summary>
    /// 自动压缩上下文：历史估算 token 超过阈值时，从最早端删除完整对话块（以 user 消息为边界，
    /// 不会拆散工具调用配对），被删内容经轻量模型浓缩为「前情摘要」注入历史头部，
    /// 既守住预算又保留会话连续性。触发与结果通过返回值上报。
    /// </summary>
    // 上下文预算：历史内容字符总量上限（≈11 万 token），超出即压缩/截断
    private const int MaxToolResultChars = 60_000;   // 单条工具结果在历史中的保留上限
    private const string SummaryTag = "【前情摘要】（更早的对话已压缩为以下要点）";
    private const int SummaryMaxChars = 1_600;

    private long ContextBudgetChars => _config.Agent.ContextBudgetChars;

    /// <summary>按内容类型精确计量消息字符数（AIContent.ToString() 对工具调用/结果近乎为空，会严重低估）。</summary>
    private static long CharsOf(ChatMessage m)
    {
        long n = m.Text?.Length ?? 0;
        foreach (var c in m.Contents)
        {
            n += c switch
            {
                TextContent tc => tc.Text?.Length ?? 0,
                FunctionCallContent fc => 240 + JsonSerializer.Serialize(
                    fc.Arguments ?? new Dictionary<string, object?>()).Length,
                FunctionResultContent fr => 120 + ((fr.Result as string)?.Length
                    ?? JsonSerializer.Serialize(fr.Result ?? "").Length),
                _ => 32,
            };
        }
        return n;
    }

    private static bool IsSummaryMessage(ChatMessage m) =>
        m.Role == ChatRole.System && (m.Text ?? "").StartsWith(SummaryTag, StringComparison.Ordinal);

    /// <summary>
    /// 回合入口用：预算裁剪 + 把被裁掉的早期对话摘要化（轻量模型），摘要写入历史头部。
    /// </summary>
    private async Task<(int RemovedMessages, long EstimatedTokensBefore, long EstimatedTokensAfter, bool Summarized)>
        AutoCompactContextAsync(CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => CompactTrim(), cancellationToken);
        if (result.RemovedMessages > 0)
        {
            var summarized = await SummarizeDroppedAsync(_droppedTranscript, cancellationToken);
            _droppedTranscript = null;
            InjectSummaryIntoHistory();
            if (summarized)
            {
                try { File.WriteAllText(_sessionSummaryFile, _historySummary); } catch { }
            }
        }
        else
        {
            InjectSummaryIntoHistory();   // 恢复的会话/历史重建后也保证摘要在场
        }
        return (result.RemovedMessages, result.EstimatedTokensBefore, result.EstimatedTokensAfter, result.RemovedMessages > 0);
    }

    /// <summary>回合结束落盘用：只做预算内裁剪与巨型结果截断（不触发摘要调用）。</summary>
    private (int RemovedMessages, long EstimatedTokensBefore, long EstimatedTokensAfter) CompactTrim()
    {
        (int RemovedMessages, long EstimatedTokensBefore, long EstimatedTokensAfter) result = default;
        try
        {
            var got = _session.TryGetInMemoryChatHistory(out var history, null, null);
            if (!got || history is null || history.Count == 0)
            {
                return result;
            }

            long totalChars = history.Sum(CharsOf);
            long estBefore = totalChars / 2;
            result.EstimatedTokensBefore = estBefore;

            // 1) 头部裁剪：从最早端删完整对话块，直到剩余量落在预算内（或只剩 2 条）
            if (totalChars > ContextBudgetChars)
            {
                long kept = 0;
                var start = history.Count;
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    kept += CharsOf(history[i]);
                    start = i;
                    if (kept >= ContextBudgetChars * 6 / 10 && history[i].Role == ChatRole.User)
                    {
                        break;   // 在 user 边界切，保证配对完整
                    }
                }
                start = Math.Min(start, history.Count - 2);   // 至少保留最近 2 条
                if (start > 0 && IsSummaryMessage(history[0]))
                {
                    start = Math.Max(start, 1);   // 摘要消息永远保留在头部
                }
                if (start > 0)
                {
                    _droppedTranscript = BuildTranscript(history, start);
                    history.RemoveRange(0, start);
                    result.RemovedMessages = start;
                }
            }

            // 2) 保留段内的巨型工具结果截断封顶（FunctionResult.Result 可安全替换，结构不变）
            foreach (var msg in history)
            {
                if (IsSummaryMessage(msg)) continue;
                for (var i = 0; i < msg.Contents.Count; i++)
                {
                    if (msg.Contents[i] is FunctionResultContent frc && frc.Result is string txt && txt.Length > MaxToolResultChars)
                    {
                        msg.Contents[i] = new FunctionResultContent(frc.CallId,
                            txt[..MaxToolResultChars] + "\n…（超长结果已自动截断）");
                    }
                }
            }

            long afterChars = history.Sum(CharsOf);
            result.EstimatedTokensAfter = afterChars / 2;
            _session.SetInMemoryChatHistory(history, null, null);
        }
        catch
        {
            // 压缩失败不影响主流程
        }
        return result;
    }

    private string? _droppedTranscript;

    /// <summary>把将被裁掉的消息压成大纲文本（供摘要器浓缩；保留靠近当前的部分）。</summary>
    private static string BuildTranscript(IList<ChatMessage> history, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < count && i < history.Count; i++)
        {
            var m = history[i];
            var role = m.Role == ChatRole.User ? "用户" : m.Role == ChatRole.Assistant ? "助手" : m.Role == ChatRole.Tool ? "工具" : "系统";
            sb.Append($"[{role}] ");
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                        sb.Append(Preview(tc.Text, 400));
                        break;
                    case FunctionCallContent fc:
                        sb.Append($"调用 {fc.Name}(").Append(Preview(JsonSerializer.Serialize(fc.Arguments ?? new Dictionary<string, object?>()), 160)).Append(')');
                        break;
                    case FunctionResultContent fr:
                        sb.Append("结果:").Append(Preview(fr.Result as string ?? JsonSerializer.Serialize(fr.Result ?? ""), 200));
                        break;
                }
            }
            sb.AppendLine();
        }
        var s = sb.ToString();
        if (s.Length > 24_000)
        {
            s = "…（更早部分略）\n" + s[^24_000..];
        }
        return s;

        static string Preview(string? text, int max) =>
            string.IsNullOrWhiteSpace(text) ? "" : (text.Length > max ? text[..max] + "…" : text);
    }

    /// <summary>用轻量模型把被裁掉的对话浓缩进滚动摘要；失败时保留旧摘要（不影响主流程）。</summary>
    private async Task<bool> SummarizeDroppedAsync(string? transcript, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }
        try
        {
            var (model, client) = _router.Resolve(ModelRouter.RoleSummarize, _chatClient);
            var prev = string.IsNullOrWhiteSpace(_historySummary) ? "（无）" : _historySummary;
            var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "你是对话摘要器。把「已有摘要」与「新裁掉的对话」合并为一份连贯的滚动摘要，供助手延续工作。" +
                    "必须保留：用户的目标与偏好、关键事实与决定、已完成的工作与产出位置、未完成/待办事项。" +
                    $"直接输出摘要正文，不超过 {SummaryMaxChars / 2} 字，不要评论。"),
                new ChatMessage(ChatRole.User, $"=== 已有摘要 ===\n{prev}\n\n=== 新裁掉的对话 ===\n{transcript}\n\n请输出合并后的滚动摘要。"),
            ], cancellationToken: cancellationToken);
            var text = response.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            if (text.Length > SummaryMaxChars)
            {
                text = text[..SummaryMaxChars];
            }
            _historySummary = text.Trim();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;   // 摘要失败不影响裁剪结果
        }
    }

    /// <summary>把滚动摘要作为 System 消息注入/更新到历史头部（存在则原位替换，不存在则插入）。</summary>
    private void InjectSummaryIntoHistory()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_historySummary))
            {
                return;
            }
            if (!_session.TryGetInMemoryChatHistory(out var history, null, null) || history is null)
            {
                return;
            }
            var text = SummaryTag + "\n" + _historySummary;
            if (history.Count > 0 && IsSummaryMessage(history[0]))
            {
                history[0] = new ChatMessage(ChatRole.System, text);
            }
            else
            {
                history.Insert(0, new ChatMessage(ChatRole.System, text));
            }
            _session.SetInMemoryChatHistory(history, null, null);
        }
        catch
        {
            // 注入失败不影响主流程
        }
    }

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
            CompactTrim();
            var serialized = await _agent.SerializeSessionAsync(_session);
            var dir = Path.GetDirectoryName(_sessionFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);   // 目录缺失（索引/目录不同步）时自愈
            }
            File.WriteAllText(_sessionFile, serialized.GetRawText());
            _sessions.Touch(_sessionId);
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
                log(CoreStrings.L("已从存档恢复上次会话。", "Session restored from archive."));
                var restored = await agent.DeserializeSessionAsync(json);
                SanitizeOrphanApprovals(restored);
                return restored;
            }
            catch (Exception ex)
            {
                log(CoreStrings.L($"会话存档无法恢复（{ex.Message}），已开启新会话。",
                    $"Session archive unusable ({ex.Message}); started a new session."));
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
