using System.Text;
using AgentBrook.Agent;
using AgentBrook.Agent.Configuration;
using AgentBrook.Agent.Infrastructure;
using Microsoft.Extensions.Configuration;

// ──────────────────────────────────────────────────────────────
// AgentBrook.Agent —— 控制台宿主（薄壳）
// 智能体核心在 AgentBrook.Agent.Core（BrookAgent），本程序只负责：
// 配置加载、控制台呈现（流式文本 / 工具活动 / HITL 提示 / 斜杠命令）。
// 同一核心被 AgentBrook.Helper（跨平台 Web 客户端）复用。
// ──────────────────────────────────────────────────────────────
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables("AGENTBROOK_")
    .Build();

var config = configuration.Get<AppConfig>() ?? new AppConfig();

Console.OutputEncoding = Encoding.UTF8;
var appRoot = Workspace.LocateAppRoot();

Console.WriteLine($"""
    ╔══════════════════════════════════════════════╗
    ║   AgentBrook · 通用智能体 (MAF + .NET 10)      ║
    ╚══════════════════════════════════════════════╝
    """);

var interaction = new ConsoleInteraction();
var brook = await BrookAgent.CreateAsync(
    config, appRoot,
    interaction: interaction,
    log: Ui.Dim);

Ui.Dim($"端点 {config.LLM.BaseUrl}｜模型 {string.Join(" / ", brook.Models)}");
Ui.Dim($"工作区 {brook.WorkspaceRoot}");
Ui.Dim($"HITL 审批工具：{(brook.ApprovalTools.Count > 0 ? string.Join("、", brook.ApprovalTools) : "（无）")}");
Ui.Dim("输入 /help 查看命令，/quit 退出。\n");

while (Console.ReadLine() is { } raw)
{
    var input = raw.Trim();
    if (input.Length == 0)
    {
        continue;
    }

    if (input is "/quit" or "/exit" or "/q")
    {
        break;
    }

    switch (input)
    {
        case "/help":
            Ui.Dim("""
                命令：
                  /help          显示本帮助
                  /new           开启新会话（清空对话历史，保留长期记忆）
                  /models        列出可用模型
                  /model <名称>   切换模型（会话上下文保留）
                  /tools         列出智能体可用工具
                  /skills        列出已安装技能
                  /memory        查看当前长期记忆
                  /access        切换完全访问（审批自动通过）
                  /save          立即保存会话
                  /quit          退出（自动保存会话）

                HITL：
                  · 需审批工具执行前会暂停，输入 y 批准；回车或输入文字即拒绝（文字作为拒绝理由）。
                  · 智能体运行中可通过 ask_user 工具向你提问，直接输入回答即可。
                """);
            continue;
        case "/new":
            await brook.NewSessionAsync();
            Ui.Dim("已开启新会话（历史已清空，记忆不受影响）。");
            continue;
        case "/models":
            Ui.Dim($"可用模型：{string.Join("、", brook.Models)}（当前：{brook.CurrentModel}）");
            continue;
        case "/tools":
            foreach (var group in brook.ToolCatalog.GroupBy(t => t.Source))
            {
                Ui.Dim($"{group.Key}（{group.Count()}）：");
                foreach (var t in group)
                {
                    Ui.Dim($"  - {t.Name}");
                }
            }
            continue;
        case "/skills":
            Ui.Dim(brook.Skills.Count == 0
                ? "未安装技能。"
                : "已安装技能：\n" + string.Join("\n", brook.Skills.Select(s => $"  - {s.Name}: {s.Description}")));
            continue;
        case "/memory":
            Ui.Dim(brook.LoadMemory() is { Length: > 0 } m ? m : "（记忆为空）");
            continue;
        case "/access":
            interaction.FullAccess = !interaction.FullAccess;
            Ui.Dim(interaction.FullAccess
                ? "♾ 已开启完全访问：后续审批自动通过（重启后复位）"
                : "已关闭完全访问：审批恢复正常逐次确认");
            continue;
            await brook.SaveSessionAsync();
            Ui.Dim("会话已保存。");
            continue;
    }

    if (input.StartsWith("/model", StringComparison.Ordinal))
    {
        var arg = input["/model".Length..].Trim();
        if (arg.Length == 0)
        {
            Ui.Dim($"当前模型：{brook.CurrentModel}。用法：/model <名称>（/models 查看）");
        }
        else
        {
            try
            {
                brook.SetModel(arg);
                Ui.Dim($"已切换模型：{brook.CurrentModel}（会话上下文保留）");
            }
            catch (InvalidOperationException ex)
            {
                Ui.Warn(ex.Message);
            }
        }
        continue;
    }

    // ── 正式对话：消费核心事件流 ──
    Ui.AgentHeader(brook.Name, brook.CurrentModel);
    try
    {
        await foreach (var e in brook.RunAsync(input))
        {
            switch (e)
            {
                case BrookEvent.TextDelta delta:
                    Console.Write(delta.Text);
                    break;
                case BrookEvent.ToolStarted tool:
                    Ui.Dim($"\n ⚙ 调用工具 {tool.Name} …");
                    break;
                case BrookEvent.Status { State: BrookRunState.Thinking }:
                    Ui.Dim("···");
                    break;
                case BrookEvent.Status { State: BrookRunState.AwaitingApproval, Detail: not null } awaiting:
                    Ui.Dim($"⏸ 等待审批：{awaiting.Detail}");
                    break;
                case BrookEvent.Failed fail:
                    Ui.Warn($"\n运行出错：{fail.Message}");
                    break;
                case BrookEvent.Usage u:
                    Ui.Dim($"\n🎯 本轮 Token 输入 {u.TurnInput} / 输出 {u.TurnOutput} / 合计 {u.TurnTotal} ｜ 累计 {u.CumTotal}");
                    break;
            }
        }
        Console.WriteLine('\n');
    }
    catch (Exception ex)
    {
        Ui.Warn($"\n运行出错：{ex.Message}");
    }
}

await brook.SaveSessionAsync();
await brook.DisposeAsync();
Ui.Dim("会话已保存，再见。");
return 0;
