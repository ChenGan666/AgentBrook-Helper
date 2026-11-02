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
