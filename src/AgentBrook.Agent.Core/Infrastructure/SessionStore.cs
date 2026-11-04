using System.Text.Json;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>一条会话元数据。</summary>
public sealed record SessionMeta(string Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// 多会话存储：workspace/sessions/&lt;id&gt;/（session.json + session-summary.txt 成对）+ index.json 元数据。
/// 首次初始化时自动把旧版单会话文件（workspace/session.json）迁移为第一个会话条目。
/// S7 加固（implementation-plan §11.1）：内部锁串行化读改写；索引 tmp+rename 原子写且失败上抛；
/// 新增 <see cref="Create(string, string)"/> 支持 IM 管道预分配会话 id（§11.2 创建意图）。
/// </summary>
public sealed class SessionStore
{
    private readonly object _gate = new();
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
    public SessionMeta Create(string title) => Create(NewId(), title);

    /// <summary>
    /// 以预分配 id 新建会话条目（IM 管道创建意图 §11.2：id 先落库再创建）。
    /// id 已存在时返回既有条目（幂等），不抛错——创建意图重放安全。
    /// </summary>
    public SessionMeta Create(string id, string title)
    {
        lock (_gate)
        {
            var existing = _index.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }
            var meta = new SessionMeta(id, title, DateTime.Now, DateTime.Now);
            _index.Add(meta);
            SaveIndex();
            Directory.CreateDirectory(SessionDir(meta.Id));
            return meta;
        }
    }

    public void Touch(string id)
    {
        lock (_gate)
        {
            var i = _index.FindIndex(m => m.Id == id);
            if (i < 0) return;
            _index[i] = _index[i] with { UpdatedAt = DateTime.Now };
            SaveIndex();
        }
    }

    public void SetTitle(string id, string title)
    {
        lock (_gate)
        {
            var i = _index.FindIndex(m => m.Id == id);
            if (i < 0) return;
            _index[i] = _index[i] with { Title = title, UpdatedAt = DateTime.Now };
            SaveIndex();
        }
    }

    /// <summary>删除会话（目录 + 条目）；目标不存在时静默返回。</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            _index.RemoveAll(m => m.Id == id);
            SaveIndex();
            try { Directory.Delete(SessionDir(id), recursive: true); } catch { }
        }
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

    /// <summary>
    /// 索引持久化：tmp + rename 原子替换；失败上抛（S7 §11.1——旧实现静默吞错会丢索引且无感知）。
    /// rename 同目录覆盖在桌面文件系统上原子；避免"半截 index.json"幽灵态。
    /// </summary>
    private void SaveIndex()
    {
        var tmp = _indexFile + ".tmp";
        File.WriteAllText(tmp,
            JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _indexFile, overwrite: true);
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
