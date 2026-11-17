namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 一个任务（回合）的最终交付产物元数据：随 transcript.jsonl 中对应 assistant 记录持久化，
/// 历史会话回放时据此还原产物展示区。
/// </summary>
public sealed record TaskArtifacts(
    /// <summary>任务标识：会话 ID + 回合内序号（仅作追溯用，不保证全局唯一约束）。</summary>
    string TaskId,
    /// <summary>任务工作目录绝对路径（任务目录 tasks/&lt;slug&gt;/；无任务目录时为工作区根）。</summary>
    string WorkingDirectory,
    /// <summary>最终交付文件清单（file = 本地文件；link = 远程媒体 URL）。</summary>
    List<ArtifactFile> Files)
{
    /// <summary>产物非空（序列化/展示判断统一入口）。</summary>
    public bool HasFiles => Files is { Count: > 0 };

    /// <summary>
    /// 跨回合聚合会话全量产物（右侧「任务产物」面板数据源）：按工作目录分组，
    /// 组内文件按 FilePath 去重（后出现覆盖先出现 = 同路径取最新记录，顺序保持首现），
    /// 组按末次出现顺序倒序（最新任务在前；首现早但末现新的组仍排前），组 TaskId 取末次记录。纯函数无副作用。
    /// </summary>
    public static IReadOnlyList<TaskArtifacts> Aggregate(IEnumerable<TaskArtifacts?> turns)
    {
        var groups = new List<Group>();
        var dirIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var ta in turns)
        {
            ordinal++;
            if (ta is null || !ta.HasFiles) continue;
            // 历史数据兜底：WorkingDirectory/FilePath 可能缺字段（JSON 反序列化为 null），拿 null 当字典 key 会炸
            if (string.IsNullOrEmpty(ta.WorkingDirectory)) continue;   // 显示增强：脏数据直接跳过
            if (!dirIndex.TryGetValue(ta.WorkingDirectory, out var gi))
            {
                gi = groups.Count;
                dirIndex[ta.WorkingDirectory] = gi;
                groups.Add(new Group(ta.WorkingDirectory));
            }
            var g = groups[gi];
            g.LastOrdinal = ordinal;
            g.LastTaskId = ta.TaskId ?? "";
            foreach (var f in ta.Files)
            {
                if (string.IsNullOrEmpty(f.FilePath)) continue;
                if (g.FileIndex.TryGetValue(f.FilePath, out var slot))
                {
                    g.Files[slot] = f;   // 同路径：最新记录覆盖，位置不变
                }
                else
                {
                    g.FileIndex[f.FilePath] = g.Files.Count;
                    g.Files.Add(f);
                }
            }
        }
        return groups
            .OrderByDescending(g => g.LastOrdinal)   // 末次出现倒序 = 最新任务在前
            .Select(g => new TaskArtifacts(g.LastTaskId, g.Dir, g.Files))
            .ToList();
    }

    private sealed class Group(string dir)
    {
        public string Dir { get; } = dir;
        public string LastTaskId { get; set; } = "";
        public int LastOrdinal { get; set; }
        public List<ArtifactFile> Files { get; } = new();
        public Dictionary<string, int> FileIndex { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>单个交付产物：本地文件或远程链接。</summary>
public sealed record ArtifactFile(
    /// <summary>文件名（含扩展名）；link 时为资源名或 URL 尾段。</summary>
    string FileName,
    /// <summary>file = 绝对路径；link = http(s) URL。</summary>
    string FilePath,
    /// <summary>简短用途说明（register_artifact 登记优先，缺失时由规则生成）。</summary>
    string? Description,
    /// <summary>扩展名（不含点，小写）；link 固定为 "link"。</summary>
    string FileType,
    /// <summary>"file" 本地文件 | "link" 远程资源。</summary>
    string Kind,
    /// <summary>文件字节数；link 为 0。</summary>
    long Size = 0);
