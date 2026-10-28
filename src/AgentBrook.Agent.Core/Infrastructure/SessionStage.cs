using System.Text.Json;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>会话阶段：按话题边界切分的一段对话。Closed 后 Summary 会注入上下文头部替代原文；
/// StartHint 为开启该阶段的用户消息开头（UI 侧回话锚定/点击定位用）。</summary>
public sealed record SessionStage(int Id, string Title, string Summary, bool Closed, string StartHint = "");

/// <summary>
/// 阶段摘要存储：workspace/sessions/&lt;id&gt;/session-stages.json。
/// 模型每回合的用户消息经轻量分类判定是否开启新阶段；关闭阶段附 ~80 字摘要，
/// 注入上下文头部后旧阶段原文可被压缩移除——比整段滚动摘要粒度更细、不易串题。
/// </summary>
public sealed class SessionStageStore
{
    private readonly string _file;

    public List<SessionStage> Stages { get; } = [];

    /// <summary>当前未关闭阶段（无则 null）。</summary>
    public SessionStage? Current => Stages.LastOrDefault(s => !s.Closed);

    public SessionStageStore(string sessionDir)
    {
        _file = Path.Combine(sessionDir, "session-stages.json");
        Load();
    }

    /// <summary>开启新阶段（返回新阶段）。</summary>
    public SessionStage OpenNew(string title, string startHint = "")
    {
        var stage = new SessionStage(Stages.Count + 1, title, "", false, startHint);
        Stages.Add(stage);
        Save();
        return stage;
    }

    /// <summary>关闭当前阶段并写入摘要（无未关闭阶段则忽略）。</summary>
    public void CloseCurrent(string summary)
    {
        var i = Stages.FindLastIndex(s => !s.Closed);
        if (i < 0)
        {
            return;
        }
        Stages[i] = Stages[i] with { Closed = true, Summary = summary };
        Save();
    }

    /// <summary>已关闭阶段清单（供注入上下文头部）。</summary>
    public IReadOnlyList<SessionStage> Closed => Stages.Where(s => s.Closed).ToList();

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }
            var loaded = JsonSerializer.Deserialize<List<SessionStage>>(File.ReadAllText(_file));
            if (loaded is not null)
            {
                Stages.AddRange(loaded);
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(Stages));
        }
        catch { }
    }
}
