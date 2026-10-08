using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Mcp;
using AgentBrook.Agent.Team;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>组装 ChatClientAgent：模型客户端 + 工具面 + 上下文提供者。</summary>
public static class AgentFactory
{
    /// <summary>按显式供应商参数创建 IChatClient（模型管理/多供应商切换用）。</summary>
    public static IChatClient CreateChatClient(string baseUrl, string apiKey, string defaultModel)
    {
        var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
        return client.GetChatClient(defaultModel).AsIChatClient();
    }

    /// <summary>创建基于 OpenAI 兼容端点（DeepSeek）的 IChatClient。</summary>
    public static IChatClient CreateChatClient(AppConfig config)
    {
        var apiKey = ResolveApiKey(config);
        var endpoint = new Uri(config.LLM.BaseUrl);

        var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = endpoint });
        return client.GetChatClient(config.LLM.DefaultModel).AsIChatClient();
    }

    /// <summary>用给定的工具面与上下文提供者创建智能体。</summary>
    public static ChatClientAgent CreateAgent(
        AppConfig config,
        IChatClient chatClient,
        IReadOnlyList<AITool> tools,
        string skillSummary,
        AIContextProvider memoryProvider)
    {
        // 技能清单拼进系统指令：模型随时知道有哪些技能可加载（渐进式披露的元数据层）。
        // 运行环境块按当前 OS 自动生成，让模型用对命令语法（Windows cmd/python、macOS zsh/python3）。
        var instructions = config.Agent.Instructions.Trim() + "\n\n" + skillSummary
            + "\n\n" + PlatformEnvironment.DescribeBlock().Trim();

        // 语言指令放在系统提示词最末尾（最高权重位置）：模型对结尾指令的遵循度远高于中部长篇规则
        var langDirective = config.Agent.ReplyLanguage == "en"
            ? "# Reply language (highest priority)\nAlways respond in English, regardless of the language the user writes in. Never mirror the user's language."
            : "# 回复语言（最高优先级）\n始终用中文回复，无论用户使用什么语言书写，都不要跟随用户的语言。";
        instructions += "\n\n" + langDirective;

        var chatOptions = new ChatOptions
        {
            Instructions = instructions,
            Tools = [.. tools],
        };
        ApplyReasoningEffort(config, chatOptions);

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = config.Agent.Id,
            Name = config.Agent.Name,
            Description = config.Agent.Description,
            ChatOptions = chatOptions,
            AIContextProviders = [memoryProvider],
        });
    }

    /// <summary>把配置的推理力度映射到 MEAI 的 ChatOptions.Reasoning（OpenAI 兼容端点 → reasoning_effort）。</summary>
    private static void ApplyReasoningEffort(AppConfig config, ChatOptions chatOptions)
    {
        if (string.IsNullOrWhiteSpace(config.LLM.ReasoningEffort))
        {
            return;
        }
        if (!Enum.TryParse(config.LLM.ReasoningEffort, ignoreCase: true, out ReasoningEffort effort))
        {
            throw new InvalidOperationException(
                $"LLM:ReasoningEffort 无效值“{config.LLM.ReasoningEffort}”，可选：none/low/medium/high/extrahigh。");
        }

        chatOptions.Reasoning ??= new ReasoningOptions();
        chatOptions.Reasoning.Effort = effort;
    }

    /// <summary>
    /// 创建项目团队中的工作 Agent：文件工具 + 团队通讯，按 caps 追加 shell / 白名单 MCP。
    /// shell 的工作目录与文件沙箱同为项目根；所有 shell 命令记入项目审计日志。
    /// mcpConnections 为主 Agent 启动时建立的共享连接（worker 侧按白名单过滤放行）。
    /// </summary>
    public static Microsoft.Agents.AI.ChatClientAgent CreateWorkerAgent(
        AppConfig config,
        IChatClient chatClient,
        string projectRoot,
        string workerName,
        string role,
        string instructions,
        List<McpConnection>? mcpConnections = null,
        WorkerCaps? caps = null)
    {
        var projectWorkspace = new Workspace(projectRoot);
        var fileTools = new Tools.FileTools(projectWorkspace, config.Agent.MaxToolOutputChars);

        var toolList = new List<AITool>
        {
            AIFunctionFactory.Create(fileTools.list_dir),
            AIFunctionFactory.Create(fileTools.read_file),
            AIFunctionFactory.Create(fileTools.write_file),
            AIFunctionFactory.Create(fileTools.append_file),
        };

        // 团队通讯（消息总线挂在项目 messages/ 目录）
        var bus = new Team.TeamMessageBus(Path.Combine(projectRoot, "messages"));
        toolList.Add(AIFunctionFactory.Create(bus.send_message));
        toolList.Add(AIFunctionFactory.Create(bus.check_messages));

        var extra = "";
        if (caps is { Shell: true })
        {
            var auditFile = Path.Combine(projectRoot, "messages", "audit.log");
            var shell = new Tools.ShellTools(projectWorkspace, config.Agent.ShellTimeoutSeconds, config.Agent.MaxToolOutputChars,
                cmd =>
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(auditFile)!);
                        File.AppendAllText(auditFile, $"{DateTime.Now:HH:mm:ss} [{workerName}] {cmd}\n");
                    }
                    catch { }
                });
            toolList.Add(AIFunctionFactory.Create(shell.run_command));
            extra += "\n- 你拥有 shell 工具（run_command，工作目录 = 项目根）：可执行构建、查询、脚本等命令。所有命令会记入审计日志，破坏性操作务必谨慎。";
        }
        if (caps is { McpServers.Count: > 0 })
        {
            // worker 作用域 MCP：共享主连接，但只放行被授权的服务器
            var mcp = new Mcp.McpGateTools(mcpConnections, config.Agent.MaxToolOutputChars, caps.McpServers);
            toolList.Add(AIFunctionFactory.Create(mcp.mcp_call));
            toolList.Add(AIFunctionFactory.Create(mcp.mcp_tool_help));
            extra += "\n# 可用 MCP 工具（仅限被授权的服务器）\n" + mcp.BuildCatalogBlock();
        }

        var chatOptions = new ChatOptions
        {
            Instructions = $"""
                你是项目团队中的工作 Agent「{workerName}」，角色：{role}。

                # 工作环境
                - 项目根：你的文件工具以此为沙箱。你的专属目录是 workers/{workerName}/（把产出写在这里）。
                - shared/ 是团队共享交付区：需要与其他成员共享的产出写进去，并在留言中告知位置。
                - 消息工具：send_message 给协调者（to 填 orchestrator）或其他成员留言；check_messages 取件。
                {extra}

                # 工作准则
                1. 专注完成分派的任务，产出写入自己的工作目录或 shared/。
                2. 完成后输出最终成果摘要（这部分会返回给协调者）。
                3. 需要其他成员配合或信息时，用 send_message 留言，并继续做你能做的部分。
                4. 遇到无法自行解决的阻塞，如实说明。
                """,
            Tools = [.. toolList],
        };

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = $"worker-{workerName}",
            Name = $"worker-{workerName}",
            Description = role,
            ChatOptions = chatOptions,
        });
    }

    private static string ResolveApiKey(AppConfig config)
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            key = config.LLM.ApiKey;
        }
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("sk-请", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "未配置 API Key：请设置环境变量 DEEPSEEK_API_KEY，或在 appsettings.json 的 LLM:ApiKey 中填写。");
        }
        return key;
    }
}
