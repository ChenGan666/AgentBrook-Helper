using System.Text.Json;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Runtime;

/// <summary>投影修复结果（恢复对账/测试断言用）。</summary>
public sealed record ProjectionRepairResult(
    int TruncatedTailBytes,     // 截掉的残行字节数（尾部三态之"不完整"）
    int AddedNewlines,          // 补的尾换行数（尾部三态之"完整无换行"）
    int AppendedEvents);        // 按 (TurnId, EventIndex) 补写的条数

/// <summary>
/// transcript.jsonl 显示投影（设计 §11.5）：
/// 可恢复的显示投影，不承担提交判据；权威事件批次在 turn_events。
/// 行格式：新行携带 turnId/ei 字段；旧记录无 turnId 字段保持兼容（只读保留，不参与补写判定）。
/// 尾部三态恢复（v4.3）：完整+换行 → 继续追加；完整无换行 → 先补换行；不完整 → 截残行再补写。
/// </summary>
public static class Projection
{
    private static string TranscriptPath(string sessionDir) => Path.Combine(sessionDir, "transcript.jsonl");

    /// <summary>解析无键转写行（BrookAgent 对账写入形态：{"role":..,"text":..}）。</summary>
    private static bool TryParseKeylessLine(string line, out string role, out string text)
    {
        role = ""; text = "";
        var t = line.Trim();
        if (t.Length == 0 || t[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("turnId", out _)) return false;   // 有键行走键判重
            role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
            text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            return role.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>新投影行（turnId+ei 指纹键）。不改主仓 BuildTranscriptList 的 role/text 语义。</summary>
    private static string SerializeLine(TurnEvent e)
    {
        if (e.ArtifactsJson is null)
            return $$"""{"turnId":{{JsonSerializer.Serialize(e.TurnId)}},"ei":{{e.EventIndex}},"role":{{JsonSerializer.Serialize(e.Role)}},"text":{{JsonSerializer.Serialize(e.Text)}}}""";
        return $$"""{"turnId":{{JsonSerializer.Serialize(e.TurnId)}},"ei":{{e.EventIndex}},"role":{{JsonSerializer.Serialize(e.Role)}},"text":{{JsonSerializer.Serialize(e.Text)}},"artifacts":{{e.ArtifactsJson}}}""";
    }

    /// <summary>按序追加事件（调用方保证语义顺序；幂等性由 Repair/恢复路径保证）。</summary>
    public static void AppendEvents(string sessionId, string rootPath, IReadOnlyList<TurnEvent> events)
    {
        if (events.Count == 0) return;
        var sessionDir = Path.Combine(rootPath, "sessions", SessionFileSystem.SessionDirName(sessionId));
        Directory.CreateDirectory(sessionDir);
        using var fs = new FileStream(TranscriptPath(sessionDir), FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(fs) { AutoFlush = false };
        foreach (var e in events)
        {
            writer.Write(SerializeLine(e));
            writer.Write('\n');
        }
        writer.Flush();
    }

    /// <summary>
    /// 投影对账修复：先按尾部三态处理文件尾，再按 (TurnId, EventIndex) 补写缺失条目。
    /// 以文件内容为事实来源（watermark 仅优化）——重复恢复结果幂等（D10）。
    /// </summary>
    public static ProjectionRepairResult Repair(string sessionId, string rootPath, IReadOnlyList<TurnEvent> authoritative)
        => RepairIn(sessionId, Path.Combine(rootPath, "sessions"), authoritative);

    /// <summary>
    /// 抑制投影落盘（Helper 宿主形态）：transcript.jsonl 由 BrookAgent 游标机制<b>单一写者</b>管理——
    /// IM 提交后的可见内容由宿主"重载+对账"喂给 BrookAgent（重载读 MAF CURRENT → Reconcile 追加缺失行）。
    /// 双写者（投影键行 + BrookAgent 行）混写会让对账尾签名失配 → 切换会话全量重追加（对话重复）。
    /// Runtime 默认 false：S2~S6 投影测试与恢复语义不受影响。
    /// </summary>
    public static bool SuppressTranscriptProjection { get; set; }

    public static ProjectionRepairResult RepairIn(string sessionId, string sessionsRoot, IReadOnlyList<TurnEvent> authoritative)
    {
        if (SuppressTranscriptProjection) return new ProjectionRepairResult(0, 0, 0);   // 单一写者：宿主负责显示投影

        var sessionDir = Path.Combine(sessionsRoot, SessionFileSystem.SessionDirName(sessionId));
        Directory.CreateDirectory(sessionDir);
        var path = TranscriptPath(sessionDir);

        var truncated = 0;
        var addedNewlines = 0;

        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');

            if (lastNewline < 0)
            {
                // 整个文件是一个无换行行
                if (IsCompleteJsonLine(bytes))
                {
                    AppendNewline(path);   // 完整但无尾换行 → 先补换行（防两条 JSON 拼接）
                    addedNewlines++;
                }
                else
                {
                    WriteTruncated(path, 0); // 不完整 → 全截
                    truncated = bytes.Length;
                }
            }
            else
            {
                var tailLen = bytes.Length - lastNewline - 1;
                if (tailLen > 0)
                {
                    var tail = bytes[(lastNewline + 1)..];
                    if (IsCompleteJsonLine(tail))
                    {
                        AppendNewline(path);          // 完整 JSON 无尾换行
                        addedNewlines++;
                    }
                    else
                    {
                        WriteTruncated(path, lastNewline + 1); // 不完整残行 → 截掉
                        truncated = tailLen;
                    }
                }
                // tailLen == 0 → 完整且有换行，正常
            }
        }

        // 收集文件中已存在的 (turnId, ei)（以修复后的文件为准；无 turnId 的旧记录不算）
        var present = new HashSet<(string TurnId, int Index)>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (TryParseProjectedLine(line, out var turnId, out var index))
                    present.Add((turnId, index));
            }
        }

        // 补写缺失（保持权威顺序）
        var toAppend = authoritative
            .Where(e => e.IsValid && !present.Contains((e.TurnId, e.EventIndex)))
            .Order(Comparer<TurnEvent>.Create((a, b) =>
                a.TurnId != b.TurnId ? string.CompareOrdinal(a.TurnId, b.TurnId) : a.EventIndex.CompareTo(b.EventIndex)))
            .ToList();

        if (toAppend.Count > 0)
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs) { AutoFlush = false };
            foreach (var e in toAppend)
            {
                writer.Write(SerializeLine(e));
                writer.Write('\n');
            }
            writer.Flush();
        }

        return new ProjectionRepairResult(truncated, addedNewlines, toAppend.Count);
    }

    /// <summary>读入投影中所有携带 turnId/ei 的行（恢复对账用）。</summary>
    public static IReadOnlyList<TurnEvent> ReadProjectedEvents(string sessionId, string rootPath)
    {
        var path = TranscriptPath(Path.Combine(rootPath, "sessions", SessionFileSystem.SessionDirName(sessionId)));
        if (!File.Exists(path)) return [];
        var list = new List<TurnEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (TryParseProjectedLine(line, out var turnId, out var index))
            {
                using var doc = JsonDocument.Parse(line);
                var role = doc.RootElement.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var artifacts = doc.RootElement.TryGetProperty("artifacts", out var a)
                    ? a.GetRawText()
                    : null;
                list.Add(new TurnEvent(turnId, index, role, text, artifacts));
            }
        }
        return list;
    }

    private static bool TryParseProjectedLine(string line, out string turnId, out int index)
    {
        turnId = ""; index = -1;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("turnId", out var t) || t.ValueKind != JsonValueKind.String) return false;
            if (!doc.RootElement.TryGetProperty("ei", out var i) || i.ValueKind != JsonValueKind.Number) return false;
            turnId = t.GetString()!;
            index = i.GetInt32();
            return turnId.Length > 0 && index >= 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCompleteJsonLine(byte[] bytes)
    {
        try
        {
            // 投影行是 UTF-8 JSON；完整 = 可解析（截断的多字节字符/半行 JSON 均不可解析）
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AppendNewline(string path)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        fs.WriteByte((byte)'\n');
    }

    private static void WriteTruncated(string path, long keepBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        fs.SetLength(keepBytes);
    }
}
