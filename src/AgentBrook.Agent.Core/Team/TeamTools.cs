using System.Text;
using System.ComponentModel;
using System.Text.Json;
using AgentBrook.Agent.Configuration;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Team;

/// <summary>
/// 主 Agent 的项目团队工具：分解目标 → 征询用户 → 派生工作 Agent → 分派任务 → 汇总交付。
/// start_project 前其他团队工具不可用（未激活会给出引导）。
/// spawn_worker 可按需授予 shell / MCP 能力：受配置天花板约束，且每次授予需用户批准。
/// </summary>
public sealed class TeamTools
{
    private TeamManager? _team;
    private readonly string _projectsRoot;
    private readonly string _capabilityCeiling;
    private readonly List<string> _mcpServers;
    private readonly IBrookInteraction _interaction;
    private readonly Func<string, string, string, string, WorkerCaps?, Microsoft.Agents.AI.AIAgent> _workerFactory;
    private readonly Action<string> _notify;

    public TeamTools(string workspaceRoot,
        Configuration.AppConfig config,
        IBrookInteraction interaction,
        Func<string, string, string, string, WorkerCaps?, Microsoft.Agents.AI.AIAgent> workerFactory,
        Action<string> notify)
    {
        _projectsRoot = Path.Combine(workspaceRoot, "projects");
        _capabilityCeiling = config.Agent.WorkerCapabilityCeiling;
        _mcpServers = config.MCP.Servers.Select(s => s.Name).ToList();
        _interaction = interaction;
        _workerFactory = workerFactory;
        _notify = notify;
    }

    public bool HasActiveProject => _team is not null;

    [Description("开启一个项目：为当前工作目标创建项目目录（含各成员工作区与共享区）。接到复杂目标、预计需要多角色协作时，先分解目标并与用户确认派生方案，再调用此工具。")]
    public string start_project(
        [Description("项目简称（小写英文连字符，如 market-research）")] string projectId,
        [Description("一句话工作目标")] string goal)
    {
        if (_team is not null)
        {
            return $"已有进行中的项目（{_team.Goal}）。请先完成或让用户确认后再开启新项目。";
        }
        var slug = new string(projectId.Trim().ToLowerInvariant().Replace(' ', '-').Select(c =>
            char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');
        if (slug.Length < 2)
        {
            return "projectId 无效（至少 2 个有效字符）";
        }
        var root = Path.Combine(_projectsRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{slug}");
        _team = new TeamManager(root, goal, _workerFactory, m => _notify(m));
        return $"✔ 项目已开启：{root}\n目标：{goal}\n现在可以用 spawn_worker 派生工作 Agent。";
    }

    [Description("派生一个工作 Agent：为其分配专属工作区、角色与指令。派生前应已通过 ask_user 与用户确认派生方案。" +
        "capabilities 非空时（授予 shell/MCP）需要用户批准该次能力授予。")]
    public async Task<string> spawn_worker(
        [Description("工作 Agent 名（小写英文连字符，如 researcher-a）")] string name,
        [Description("角色定位，如：市场调研员")] string role,
        [Description("该 Agent 的详细工作指令：职责、产出要求、注意事项")] string instructions,
        [Description("授予的额外能力列表，可含 \"shell\"（执行命令）、\"mcp\"（全部已配置 MCP 服务器）、\"mcp:服务器名\"（指定服务器，可多个）。留空 = 仅文件工具（无需批准）。")] string[]? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        if (_team is null)
        {
            return "尚未开启项目：请先 start_project。";
        }

        var (caps, capsError) = WorkerCaps.Parse(capabilities, _capabilityCeiling, _mcpServers);
        if (capsError is not null)
        {
            return $"派生被拒绝：{capsError}";
        }

        try
        {
            if (caps is { Any: true })
            {
                // 能力授予需用户批准（完全访问模式自动通过；后台场景弹气泡快捷卡并响提示音）
                var grantJson = JsonSerializer.Serialize(new
                {
                    name = name.Trim(),
                    capabilities = caps.Describe(),
                });
                var decision = await _interaction.GetApprovalAsync("spawn_worker", grantJson, cancellationToken);
                if (!decision.Approved)
                {
                    return $"派生「{name.Trim()}」被拒绝：{decision.Reason ?? "用户未批准能力授予"}。可去掉 capabilities 重新派生（仅文件工具，无需批准）。";
                }
            }

            var worker = _team.SpawnWorker(name.Trim(), role.Trim(), instructions.Trim(), caps);
            var granted = caps is { Any: true } ? $"\n已授予能力：{caps!.Describe()}（已记录审计日志）" : "";
            return $"✔ 已派生「{worker.Name}」（{worker.Role}），工作区 workers/{worker.Name}/。用 assign_task 分派任务。{granted}";
        }
        catch (Exception ex)
        {
            return $"派生失败：{ex.Message}";
        }
    }

    [Description("把一项具体任务分派给某个工作 Agent，等待其完成并返回成果。任务应清晰、可独立完成；其产出在 workers/<名字>/ 或 shared/ 中。")]
    public async Task<string> assign_task(
        [Description("工作 Agent 名")] string workerName,
        [Description("要完成的任务描述，尽量具体（含期望产出与验收标准）")] string task)
    {
        if (_team is null)
        {
            return "尚未开启项目。";
        }
        var worker = _team.GetWorker(workerName);
        if (worker is null)
        {
            return $"工作 Agent {workerName} 不存在。现有：{string.Join("、", _team.WorkerNames)}";
        }

        // 收件箱消息随任务一并带入（其他成员的留言）
        var pending = _team.TakeMessages(worker.Name);
        var enriched = task;
        if (pending.Count > 0)
        {
            enriched += "\n\n[其他成员的留言]\n" + string.Join("\n",
                pending.Select(m => $"- {m.From}: {m.Text}"));
        }

        _notify($"🧵 {worker.Name} 开始执行任务…");
        var response = await worker.Agent.RunAsync(enriched, worker.Session);
        var answer = response.Text ?? "";
        worker.CompletedTasks.Add(task);

        // 识别其留给协调者的留言
        var inboxForOrchestrator = _team.TakeMessages("orchestrator");
        var notes = string.Join("\n", inboxForOrchestrator.Select(m => $"[{m.From}] {m.Text}"));

        return $"【{worker.Name} 任务完成】\n{answer}" +
               (notes.Length > 0 ? $"\n\n[成员留言]\n{notes}" : "");
    }

    [Description("向某个工作 Agent 发送实时消息（补充信息、变更要求、成员间协作），对方会立即处理并回复。")]
    public async Task<string> send_to_worker(
        [Description("目标工作 Agent 名")] string workerName,
        [Description("消息内容")] string message)
    {
        if (_team is null)
        {
            return "尚未开启项目。";
        }
        var worker = _team.GetWorker(workerName);
        if (worker is null)
        {
            return $"工作 Agent {workerName} 不存在。现有：{string.Join("、", _team.WorkerNames)}";
        }
        var response = await worker.Agent.RunAsync(
            $"[来自协调者的实时消息] {message}", worker.Session);
        return $"【{worker.Name} 回复】{response.Text}";
    }

    [Description("向所有工作 Agent 广播同一条消息（如目标变更、新的共享资料位置）。")]
    public async Task<string> broadcast_to_workers(
        [Description("广播内容")] string message)
    {
        if (_team is null || _team.WorkerNames.Count == 0)
        {
            return "尚无工作 Agent。";
        }
        var replies = new StringBuilder();
        foreach (var name in _team.WorkerNames)
        {
            var worker = _team.GetWorker(name)!;
            var response = await worker.Agent.RunAsync($"[广播] {message}", worker.Session);
            replies.AppendLine($"- {name}: {response.Text}");
        }
        return $"【广播已达 {count_replies(replies.ToString())} 个成员】\n{replies}";
    }

    private static int count_replies(string s) => s.Split('\n').Length;

    [Description("查看当前项目的团队成员与状态。")]
    public string list_workers()
    {
        if (_team is null)
        {
            return "尚未开启项目。接到复杂目标时可先 start_project 并派生团队。";
        }
        var sb = new StringBuilder($"项目目标：{_team.Goal}\n项目目录：{_team.ProjectRoot}\n成员：");
        foreach (var name in _team.WorkerNames)
        {
            var w = _team.GetWorker(name)!;
            sb.Append($"\n- {name}（{w.Role}）已完成任务 {w.CompletedTasks.Count} 项");
            if (w.Caps is { Any: true })
            {
                sb.Append($"｜能力：{w.Caps.Describe()}");
            }
        }
        return sb.ToString();
    }

    [Description("读取共享区文件清单或指定文件内容（shared/ 为团队共享交付区）。path 留空列出清单。")]
    public string read_shared([Description("相对 shared/ 的文件路径，留空列目录")] string? path = null)
    {
        if (_team is null)
        {
            return "尚未开启项目。";
        }
        var shared = _team.SharedDir;
        if (string.IsNullOrWhiteSpace(path))
        {
            var files = Directory.EnumerateFiles(shared, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(shared, f)).ToList();
            return files.Count == 0 ? "（shared/ 为空）" : "shared/ 文件：\n" + string.Join("\n", files);
        }
        var full = Path.GetFullPath(Path.Combine(shared, path));
        if (!full.StartsWith(Path.GetFullPath(shared), StringComparison.Ordinal) || !File.Exists(full))
        {
            return $"文件不存在：{path}";
        }
        return File.ReadAllText(full);
    }
}
