using System.ComponentModel;
using System.Text.Json;

namespace AgentBrook.Agent.Team;

/// <summary>工作 Agent 的消息收发工具（文件总线：messages/&lt;to&gt;.inbox.jsonl）。</summary>
public sealed class TeamMessageBus(string messagesDir)
{
    private readonly string _messagesDir = messagesDir;

    [Description("给项目中的其他成员发送消息（to 填成员名：orchestrator 或某个工作 Agent 名）。")]
    public string send_message(
        [Description("目标成员名：orchestrator 或工作 Agent 名")] string to,
        [Description("消息内容")] string text)
    {
        Directory.CreateDirectory(_messagesDir);
        var inbox = Path.Combine(_messagesDir, $"{to.Trim()}.inbox.jsonl");
        var entry = JsonSerializer.Serialize(new
        {
            from = "worker",
            text,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        });
        File.AppendAllText(inbox, entry + Environment.NewLine);
        return $"已发送给 {to}。";
    }

    [Description("取出并清空你的收件箱消息（其他成员发来的留言）。")]
    public string check_messages()
    {
        var inbox = Path.Combine(_messagesDir, "orchestrator.inbox.jsonl");
        if (!File.Exists(inbox))
        {
            return "（收件箱为空）";
        }
        var lines = File.ReadAllLines(inbox).Where(l => l.Trim().Length > 0).ToList();
        File.Delete(inbox);
        return lines.Count == 0 ? "（收件箱为空）" : string.Join("\n", lines);
    }
}
