using System.Text.Json;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>一条会话元数据。</summary>
public sealed record SessionMeta(string Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// 多会话存储：workspace/sessions/&lt;id&gt;/（session.json + session-summary.txt 成对）+ index.json 元数据。
/// 首次初始化时自动把旧版单会话文件（workspace/session.json）迁移为第一个会话条目。
/// </summary>
public sealed class SessionStore
{
    private readonly string _dir;
    private readonly string _indexFile;
    private readonly string _legacySessionFile;
    private readonly string _legacySummaryFile;
    private List<SessionMeta> _index;

    public SessionStore(string sessionsDir, string legacySessionFile, string legacySummaryFile)
    {
        _dir = sessionsDir;
        Directory.CreateDirectory(_dir);
        _indexFile = Path.Combine(_dir, "index.json");
        _legacySessionFile = legacySessionFile;
        _legacySummaryFile = legacySummaryFile;
        _index = LoadIndex();
        MigrateLegacy();
        PruneOrphans();
    }

    /// <summary>清洗：目录已丢失的幽灵条目（历史中断/多实例遗留）直接移除，避免选中后保存失败。</summary>
    private void PruneOrphans()
    {
        var before = _index.Count;
        _index.RemoveAll(m => !Directory.Exists(SessionDir(m.Id)));
        if (_index.Count != before)
        {
            SaveIndex();
        }
    }

    /// <summary>按最近使用排序的会话清单。</summary>
    public List<SessionMeta> Listed =>
        [.. _index.OrderByDescending(m => m.UpdatedAt)];

    public SessionMeta? Get(string id) =>
        _index.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>新建会话条目（含目录），返回元数据。</summary>
    public SessionMeta Create(string title)
    {
        var meta = new SessionMeta(NewId(), title, DateTime.Now, DateTime.Now);
        _index.Add(meta);
        SaveIndex();
        Directory.CreateDirectory(SessionDir(meta.Id));
        return meta;
    }

    public void Touch(string id)
    {
        var i = _index.FindIndex(m => m.Id == id);
        if (i < 0) return;
        _index[i] = _index[i] with { UpdatedAt = DateTime.Now };
        SaveIndex();
    }

    public void SetTitle(string id, string title)
    {
        var i = _index.FindIndex(m => m.Id == id);
        if (i < 0) return;
        _index[i] = _index[i] with { Title = title, UpdatedAt = DateTime.Now };
        SaveIndex();
    }

    /// <summary>删除会话（目录 + 条目）；目标不存在时静默返回。</summary>
    public void Remove(string id)
    {
        _index.RemoveAll(m => m.Id == id);
        SaveIndex();
        try { Directory.Delete(SessionDir(id), recursive: true); } catch { }
    }

    public string SessionFile(string id) => Path.Combine(SessionDir(id), "session.json");
    public string SummaryFile(string id) => Path.Combine(SessionDir(id), "session-summary.txt");

    private string SessionDir(string id)
    {
        if (id.Any(c => Path.GetInvalidPathChars().Contains(c) || c == '/' || c == '\\'))
        {
            throw new ArgumentException("非法会话 ID");
        }
        return Path.Combine(_dir, id);
    }

    private static string NewId() =>
        $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

    private List<SessionMeta> LoadIndex()
    {
        try
        {
            var json = File.ReadAllText(_indexFile);
            return JsonSerializer.Deserialize<List<SessionMeta>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveIndex()
    {
        try
        {
            File.WriteAllText(_indexFile,
                JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>旧版单会话文件 → 第一个会话条目（只迁移一次；索引已存在则跳过）。</summary>
    private void MigrateLegacy()
    {
        if (_index.Count > 0 || !File.Exists(_legacySessionFile))
        {
            return;
        }
        var created = File.GetLastWriteTime(_legacySessionFile);
        var meta = new SessionMeta(NewId(), "上次会话", created, created);
        Directory.CreateDirectory(SessionDir(meta.Id));
        try
        {
            File.Move(_legacySessionFile, SessionFile(meta.Id));
            if (File.Exists(_legacySummaryFile))
            {
                File.Move(_legacySummaryFile, SummaryFile(meta.Id));
            }
            _index.Add(meta);
            SaveIndex();
        }
        catch
        {
            // 迁移失败保留原文件，走新会话
        }
    }
}
