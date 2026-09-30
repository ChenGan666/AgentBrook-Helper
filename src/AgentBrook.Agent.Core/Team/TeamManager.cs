using System.Text.Json;

namespace AgentBrook.Agent.Team;

/// <summary>一个已派生的工作 Agent 句柄。</summary>
public sealed class WorkerHandle
{
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required string WorkerDirectory { get; init; }
    public required Microsoft.Agents.AI.AIAgent Agent { get; init; }
    public required Microsoft.Agents.AI.AgentSession Session { get; init; }
    public List<string> CompletedTasks { get; } = [];
}

/// <summary>
/// 项目团队管理器：一个工作目标对应一个项目目录，
/// 主 Agent 可派生多个工作 Agent（各自独立工作区），并通过消息总线实时通讯。
/// </summary>
public sealed class TeamManager
{
    private readonly Dictionary<string, WorkerHandle> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string, string, Microsoft.Agents.AI.AIAgent> _workerFactory;
    private readonly Action<string>? _notify;

    public TeamManager(string projectRoot, string goal,
        Func<string, string, string, Microsoft.Agents.AI.AIAgent> workerFactory,
        Action<string>? notify = null)
    {
        ProjectRoot = projectRoot;
        Goal = goal;
        _workerFactory = workerFactory;
        _notify = notify;

        Directory.CreateDirectory(WorkersRoot);
        Directory.CreateDirectory(SharedDir);
        Directory.CreateDirectory(MessagesDir);

        // 项目元数据
        File.WriteAllText(Path.Combine(projectRoot, "project.json"), JsonSerializer.Serialize(new
        {
            goal,
            createdAt = DateTime.Now,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    public string ProjectRoot { get; }
    public string Goal { get; }
    public string WorkersRoot => Path.Combine(ProjectRoot, "workers");
    public string SharedDir => Path.Combine(ProjectRoot, "shared");
    public string MessagesDir => Path.Combine(ProjectRoot, "messages");

    public IReadOnlyList<string> WorkerNames => [.. _workers.Keys];

    public WorkerHandle? GetWorker(string name) =>
        _workers.TryGetValue(name, out var w) ? w : null;

    /// <summary>派生工作 Agent：创建专属工作区目录 + 轻量 Agent（角色指令 + 项目文件工具 + 通讯工具）。</summary>
    public WorkerHandle SpawnWorker(string name, string role, string instructions)
    {
        if (_workers.ContainsKey(name))
        {
            throw new InvalidOperationException($"工作 Agent {name} 已存在");
        }
        var workerDir = Path.Combine(WorkersRoot, name);
        Directory.CreateDirectory(workerDir);

        var agent = _workerFactory(name, role, instructions);
        var handle = new WorkerHandle
        {
            Name = name,
            Role = role,
            WorkerDirectory = workerDir,
            Agent = agent,
            Session = agent.CreateSessionAsync().GetAwaiter().GetResult(),
        };
        _workers[name] = handle;
        Notify($"🧵 已派生工作 Agent「{name}」（{role}），工作区：workers/{name}/");
        return handle;
    }

    /// <summary>发送消息到某成员收件箱（文件总线，接收方下次 check_messages 取件）。</summary>
    public void SendMessage(string from, string to, string text)
    {
        Directory.CreateDirectory(MessagesDir);
        var inbox = Path.Combine(MessagesDir, $"{to}.inbox.jsonl");
        var entry = JsonSerializer.Serialize(new
        {
            from,
            text,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.AppendAllText(inbox, entry + Environment.NewLine);
    }

    /// <summary>取出并清空某成员收件箱的全部消息。</summary>
    public IReadOnlyList<(string From, string Text, string Time)> TakeMessages(string member)
    {
        var inbox = Path.Combine(MessagesDir, $"{member}.inbox.jsonl");
        if (!File.Exists(inbox))
        {
            return [];
        }
        var list = new List<(string, string, string)>();
        foreach (var line in File.ReadAllLines(inbox))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(line);
                list.Add((
                    doc.RootElement.GetProperty("from").GetString() ?? "",
                    doc.RootElement.GetProperty("text").GetString() ?? "",
                    doc.RootElement.GetProperty("time").GetString() ?? ""));
            }
            catch { }
        }
        File.Delete(inbox);
        return list;
    }

    private void Notify(string text) => _notify?.Invoke(text);
}
