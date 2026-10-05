using System.ComponentModel;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Tools;

/// <summary>
/// 轻量子 Agent 委派：把一个复杂子任务交给独立的临时 Agent 执行，
/// 中间工具输出隔离在子 Agent 上下文里，只有最终结论回到主对话——
/// 这是控制主 Agent 上下文膨胀的主要手段（参考 Claude Code 的 subagent 模式）。
/// 子 Agent 只配文件类工具（无 shell、无审批），产出写入委派沙箱目录，安全且可审计。
/// </summary>
public sealed class SubAgentTools
{
    private readonly string _workspaceRoot;
    private readonly Func<(string Model, IChatClient Client)> _resolveClient;
    private int _running;

    private const int MaxResultChars = 8_000;
    private static readonly TimeSpan DelegateTimeout = TimeSpan.FromMinutes(8);

    public SubAgentTools(string workspaceRoot, Func<(string Model, IChatClient Client)> resolveClient)
    {
        _workspaceRoot = workspaceRoot;
        _resolveClient = resolveClient;
    }

    [Description("把一个复杂子任务委派给子Agent独立完成（文献调研、多文件梳理、长报告撰写、批量整理等）。" +
        "子Agent拥有工作区文件读写工具，中间过程不占用主对话上下文，只把最终结论返回。" +
        "任务描述要自包含：目标、背景事实、期望产出（写到哪里）、验收标准。" +
        "简单直接的任务（一两个工具调用就能完成）不要委派，自己做更快。")]
    public async Task<string> delegate_task(
        [Description("完整的任务描述（自包含，子Agent看不到主对话历史）")] string task,
        [Description("建议产出文件写到 delegate/ 下的哪个子目录（留空自动分配）")] string? outputDir = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return "已有一个子Agent在执行中，请等它完成后再委派下一个。";
        }
        try
        {
            var slug = Slugify(outputDir) ?? $"task-{DateTime.Now:HHmmss}";
            var scratch = Path.Combine(_workspaceRoot, "delegate", $"{DateTime.Now:yyyyMMdd}-{slug}");
            Directory.CreateDirectory(scratch);

            var (model, client) = _resolveClient();
            var agent = CreateDelegateAgent(client, scratch);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DelegateTimeout);
            var prompt = $"""
                {task}

                # 工作环境
                - 你的产出目录：delegate/{Path.GetFileName(scratch)}/（相对工作区根，你的文件工具以工作区为沙箱）。
                - 过程文件与草稿写在产出目录；最终结论直接写在回复里。
                - 只依据工具返回的真实内容工作，不要编造；拿不到的信息如实说明。

                # 输出要求
                完成后输出精炼的最终结论（不要复述过程）：核心结果、关键数据/文件位置、未尽事项。控制在 {MaxResultChars / 2} 字符以内。
                """;

            var response = await agent.RunAsync(prompt, cancellationToken: timeoutCts.Token);
            var answer = response.Text ?? "";
            if (answer.Length > MaxResultChars)
            {
                answer = answer[..MaxResultChars] + "\n…（子Agent结论过长已截断，完整产出见其产出目录）";
            }
            return $"【子Agent（{model}）完成】\n{answer}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // 主回合被打断，向调用方传播取消
        }
        catch (OperationCanceledException)
        {
            return "子Agent执行超时（8 分钟），已中止。可拆小任务后重试。";
        }
        catch (Exception ex)
        {
            return $"子Agent执行失败：{ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>轻量委派 Agent：极简指令 + 文件四件套（无 shell/记忆/审批），上下文从零开始。</summary>
    private ChatClientAgent CreateDelegateAgent(IChatClient client, string scratchDir)
    {
        var fileTools = new FileTools(new Workspace(_workspaceRoot), 24_000);
        var options = new ChatOptions
        {
            Instructions = """
                你是被主Agent委派的子Agent，独立完成一个明确的子任务。工作准则：
                1. 直接动手，用文件工具查证真实内容；不要询问、不要等待确认。
                2. 过程产出写入指定产出目录；引用数据要注明来源文件。
                3. 只输出最终结论：结果、关键数据、文件位置、未尽事项。不要寒暄。
                """,
            Tools =
            [
                AIFunctionFactory.Create(fileTools.list_dir),
                AIFunctionFactory.Create(fileTools.read_file),
                AIFunctionFactory.Create(fileTools.write_file),
                AIFunctionFactory.Create(fileTools.append_file),
            ],
        };
        return new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = "brook-delegate",
            Name = "brook-delegate",
            Description = "临时子Agent：独立完成委派任务",
            ChatOptions = options,
        });
    }

    private static string? Slugify(string? name)
    {
        var slug = new string((name ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray()).Trim('-');
        return slug.Length >= 2 ? slug : null;
    }
}
