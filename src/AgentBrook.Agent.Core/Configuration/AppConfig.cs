namespace AgentBrook.Agent.Configuration;

/// <summary>appsettings.json 的顶层配置模型。</summary>
public sealed class AppConfig
{
    public LlmOptions LLM { get; set; } = new();
    public AgentOptions Agent { get; set; } = new();
    public McpOptions MCP { get; set; } = new();
}

/// <summary>模型接入参数（OpenAI 兼容端点）。</summary>
public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>留空时优先取环境变量 DEEPSEEK_API_KEY。</summary>
    public string ApiKey { get; set; } = "";

    public string DefaultModel { get; set; } = "deepseek-flash";

    /// <summary>/model 命令可切换的模型清单（默认值由 appsettings.json 提供）。</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>
    /// 推理力度：none / low / medium / high / extrahigh；留空使用模型默认。
    /// DeepSeek 思考模型要求把 reasoning_content 回传给 API，而审批续跑等场景无法保证，
    /// 因此 DeepSeek 下建议设为 none 关闭思考模式。
    /// </summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>智能体自身参数与工具约束。</summary>
public sealed class AgentOptions
{
    public string Id { get; set; } = "brook-main";

    public string Name { get; set; } = "brook";

    public string Description { get; set; } = "AgentBrook 通用智能体：具备 Markdown 记忆、技能、文件读写、命令行与 MCP 工具。";

    public string Instructions { get; set; } = "You are a helpful assistant.";

    /// <summary>工作区根目录（相对路径基于应用根目录解析）。</summary>
    public string WorkspaceRoot { get; set; } = "workspace";

    /// <summary>Shell 命令默认超时（秒）。</summary>
    public int ShellTimeoutSeconds { get; set; } = 120;

    /// <summary>工具输出截断阈值（字符），超出部分丢弃并附加说明。</summary>
    public int MaxToolOutputChars { get; set; } = 24_000;

    /// <summary>单轮注入记忆的总字符上限。</summary>
    public int MaxMemoryChars { get; set; } = 24_000;

    /// <summary>
    /// 需要人工审批（HITL）才能执行的工具名列表，如 ["run_command"]。
    /// MCP 工具写带前缀的全名，如 "mcp_filesystem_write_file"。
    /// </summary>
    public List<string> RequireApprovalTools { get; set; } = [];

    /// <summary>
    /// 项目团队 worker 的能力天花板：none（仅文件工具）/ shell / mcp / all（默认）。
    /// spawn_worker 请求超出天花板的能力时直接拒绝。
    /// </summary>
    public string WorkerCapabilityCeiling { get; set; } = "all";

    /// <summary>技能市场索引 URL（JSON：{"skills":[{"name","description","url"}]}）。留空表示未启用。</summary>
    public string? SkillMarketUrl { get; set; }

    /// <summary>
    /// 子 Agent（delegate_task 委派）使用的模型名；留空取模型清单首个（约定为轻量模型）。
    /// </summary>
    public string? SubAgentModel { get; set; }

    /// <summary>
    /// 会话摘要压缩使用的模型名；留空取模型清单首个（约定为轻量模型）。
    /// </summary>
    public string? SummarizeModel { get; set; }

    /// <summary>历史字符总量超过该预算时触发自动压缩（摘要+裁剪）。</summary>
    public long ContextBudgetChars { get; set; } = 220_000;

    /// <summary>
    /// 模型回复语言（zh/en）。由设置界面保存、ApplyAssistantSettingsAsync 写回，
    /// AgentFactory 会把它作为系统提示词的最后一条最高优先级指令块注入。
    /// </summary>
    public string ReplyLanguage { get; set; } = "zh";
}

/// <summary>MCP 服务器接入配置（stdio 传输）。</summary>
public sealed class McpOptions
{
    public List<McpServerConfig> Servers { get; set; } = [];
}

public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];

    /// <summary>追加到子进程的环境变量；值中的 {WORKSPACE} 会被替换为工作区绝对路径。</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>工作目录；留空使用工作区根目录。支持 {WORKSPACE} 占位符。</summary>
    public string? WorkingDirectory { get; set; }
}
