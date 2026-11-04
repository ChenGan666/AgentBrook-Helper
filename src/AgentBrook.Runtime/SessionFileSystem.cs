using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Runtime;

/// <summary>版本固有元数据（meta.json；v4.2：切换 CURRENT 后永不修改——只存版本固有信息）。</summary>
public sealed record VersionMeta(
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("commitId")] string CommitId,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

/// <summary>
/// 会话文件系统（设计 §11.5 存储形态）：
/// <code>
/// &lt;root&gt;/sessions/&lt;id&gt;/versions/&lt;rev&gt;/{session.json, meta.json}
/// &lt;root&gt;/sessions/&lt;id&gt;/CURRENT            ★唯一提交判据（tmp+rename 原子替换）
/// &lt;root&gt;/sessions/&lt;id&gt;/transcript.jsonl     （可恢复显示投影；见 Projection）
/// </code>
/// 不可变性：versions/&lt;rev&gt;/ 在切换 CURRENT 之前定稿，之后永不修改。
/// 完整性判据 = meta.json 可解析且 commitId 与 commits 行匹配（meta 最后写=完成标记）。
/// </summary>
public sealed class SessionFileSystem
{
    private readonly string _root;

    public SessionFileSystem(string rootPath)
    {
        _root = Path.Combine(rootPath, "sessions");
        Directory.CreateDirectory(_root);
    }

    public string SessionDir(string sessionId) => Path.Combine(_root, SessionDirName(sessionId));

    /// <summary>会话根目录（&lt;root&gt;/sessions；恢复扫描用）。</summary>
    public string SessionsRoot => _root;

    /// <summary>sessionId 路径安全化：允许字母数字与 -_.，拒绝导航片段/分隔符（D09 防目录逃逸同源规则）。</summary>
    public static string SessionDirName(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new RuntimePipelineException(ImErrorCodes.BadPayload, "sessionId 为空。");
        foreach (var c in sessionId)
        {
            var ok = char.IsLetterOrDigit(c) || c is '-' or '_' or '.';
            if (!ok)
                throw new RuntimePipelineException(ImErrorCodes.BadPayload, $"sessionId 含非法字符：{c}");
        }
        if (sessionId is "." or ".." || sessionId.Contains(".."))
            throw new RuntimePipelineException(ImErrorCodes.BadPayload, "sessionId 含导航片段。");
        return sessionId;
    }

    private string VersionDir(string sessionId, long revision) =>
        Path.Combine(SessionDir(sessionId), "versions", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private string CurrentFile(string sessionId) => Path.Combine(SessionDir(sessionId), "CURRENT");

    /// <summary>读取 CURRENT 指向的 revision 与版本 meta；无会话/无 CURRENT → null。</summary>
    public (long Revision, VersionMeta Meta)? ReadCurrent(string sessionId)
    {
        var currentFile = CurrentFile(sessionId);
        if (!File.Exists(currentFile)) return null;
        var content = File.ReadAllText(currentFile).Trim();
        if (!long.TryParse(content, out var revision))
            throw new RuntimePipelineException(ImErrorCodes.RecoveryRequired, $"CURRENT 不可解析：'{content}'");
        var meta = ReadMeta(sessionId, revision);
        if (meta is null)
            throw new RuntimePipelineException(ImErrorCodes.RecoveryRequired, $"CURRENT 指向 {revision} 但版本不完整。");
        return (revision, meta);
    }

    /// <summary>读版本 meta；目录/meta 缺失或不可解析 → null（不完整）。</summary>
    public VersionMeta? ReadMeta(string sessionId, long revision)
    {
        var metaFile = Path.Combine(VersionDir(sessionId, revision), "meta.json");
        if (!File.Exists(metaFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<VersionMeta>(File.ReadAllText(metaFile));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>版本目录完整性判据：session.json 存在 + meta 可解析 + commitId 匹配。</summary>
    public bool IsVersionComplete(string sessionId, long revision, string commitId)
    {
        var dir = VersionDir(sessionId, revision);
        if (!File.Exists(Path.Combine(dir, "session.json"))) return false;
        var meta = ReadMeta(sessionId, revision);
        return meta is not null && meta.CommitId == commitId && meta.Revision == revision;
    }

    public byte[] ReadSnapshot(string sessionId, long revision)
    {
        var payload = File.ReadAllBytes(Path.Combine(VersionDir(sessionId, revision), "session.json"));
        return payload;
    }

    /// <summary>
    /// 写版本目录并定稿（提交协议步骤 2 的文件部分）：session.json 先写、meta.json 最后写（完成标记）。
    /// 目录已存在完整版本 → 拒绝（不可变性；重复提交防御）。
    /// </summary>
    public void WriteVersion(string sessionId, long generation, long revision, string commitId, ReadOnlySpan<byte> payload)
    {
        var dir = VersionDir(sessionId, revision);
        if (Directory.Exists(dir))
        {
            if (IsVersionComplete(sessionId, revision, commitId))
                throw new RuntimePipelineException(ImErrorCodes.RevisionConflict,
                    $"版本 {revision} 已完整存在（commitId 匹配），拒绝重写。");
            // 半成品（上次写盘中断）：作废重写（恢复对账同样会删）
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);

        ImFaultPoints.Raise(ImFaultPoints.VersionDirAfterPayload);

        var payloadPath = Path.Combine(dir, "session.json");
        using (var fs = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.Write(payload);

        var meta = new VersionMeta(generation, revision, commitId, DateTimeOffset.UtcNow.ToString("O"));
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));

        ImFaultPoints.Raise(ImFaultPoints.VersionDirAfterMeta);
    }

    /// <summary>原子切换 CURRENT → revision（tmp + rename；★快照提交唯一判据）。</summary>
    public void SwitchCurrent(string sessionId, long revision)
    {
        var currentFile = CurrentFile(sessionId);
        var tmpFile = currentFile + ".tmp";
        File.WriteAllText(tmpFile, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // POSIX rename(2) 同目录覆盖原子；.NET Core 3.0+ File.Move(overwrite) 映射 rename
        File.Move(tmpFile, currentFile, overwrite: true);
        ImFaultPoints.Raise(ImFaultPoints.CurrentAfterSwitch);
    }

    /// <summary>删除整个会话目录（删除协议步骤 4；仅 Deleting 封版后允许调用）。</summary>
    public void DeleteSessionDirectory(string sessionId)
    {
        var dir = SessionDir(sessionId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public bool SessionDirectoryExists(string sessionId) => Directory.Exists(SessionDir(sessionId));

    /// <summary>列出存在的版本目录号（恢复对账/孤儿清理用）。</summary>
    public IReadOnlyList<long> ListVersions(string sessionId)
    {
        var versionsDir = Path.Combine(SessionDir(sessionId), "versions");
        if (!Directory.Exists(versionsDir)) return [];
        return [.. Directory.GetDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Select(n => long.TryParse(n, out var r) ? r : -1)
            .Where(r => r > 0)
            .Order()];
    }

    /// <summary>删除单个版本目录（GC 专用：调用方须先满足 §11.5 版本清理约束）。</summary>
    public void DeleteVersion(string sessionId, long revision)
    {
        var dir = VersionDir(sessionId, revision);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// 故障注入点（计划 §8：异常注入用于快速单测；进程级退出由 TestHost crash 场景覆盖）。
/// 生产默认无订阅、零开销；测试通过 <see cref="OnPoint"/> 注入异常模拟崩溃点。
/// </summary>
public static class ImFaultPoints
{
    public static Action<string>? OnPoint { get; set; }

    // 提交协议（§11.5 顺序）
    public const string VersionDirAfterPayload = "commit.version-dir.after-payload";
    public const string VersionDirAfterMeta = "commit.version-dir.after-meta";
    public const string CommitTxnBefore = "commit.txn.before";
    public const string CommitTxnInside = "commit.txn.inside";
    public const string CurrentAfterSwitch = "commit.current.after-switch";
    public const string ProjectionBefore = "commit.projection.before";
    public const string ProjectionMid = "commit.projection.mid";
    public const string FinalizeTxnBefore = "commit.finalize.before";
    public const string FinalizeTxnInside = "commit.finalize.inside";

    // 非安全工具写前标记（§11.4）
    public const string UnsafeBeforeMark = "unsafe.before-mark";
    public const string UnsafeAfterMark = "unsafe.after-mark";
    public const string UnsafeAfterTool = "unsafe.after-tool";

    // 删除协议（§11.6）
    public const string DeleteAfterIntent = "delete.after-intent";
    public const string DeleteAfterDirRemoval = "delete.after-dir-removal";

    public static void Raise(string name) => OnPoint?.Invoke(name);
}
