using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 回合级产物采集器：RunAsync 开始 BeginTurn() 重置，回合结束 Finalize() 定稿。
/// 产物来源（按描述优先级）：
///   1) register_artifact 工具显式登记（path + description）；
///   2) 任务目录 tasks/&lt;slug&gt;/deliverables/ 下的本回合文件（write_file/append_file 回调 + 落盘枚举）；
///   3) 散文件兜底：任务目录顶层与工作区根目录本回合新增/修改的文件；
///   4) media_generate 成功的远程 URL（Kind=link）。
/// 排除：sessions/ memory/ skills/ context-archive/ 等系统目录、隐藏文件、.tmp/.log/.bak、工作区根配置文件。
/// </summary>
public sealed class ArtifactCollector
{
    private readonly Workspace _workspace;

    // 工具参数/结果中出现 tasks/<dir>/ 即视为本回合任务目录线索
    private static readonly Regex TaskDirRegex =
        new(@"(?:^|[/\\""'\s])tasks[/\\](?<dir>[^/\\""'\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] ExcludedTopDirs =
        ["sessions", "memory", "skills", "context-archive", "plugins", "workers", "shared", ".git"];

    private static readonly HashSet<string> ExcludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "models.json", "mcp-user.json", "session.json", "session-summary.txt", "transcript.jsonl",
        "update-check.json", "session-stages.json", "index.json",
    };

    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".log", ".bak", ".cache", ".lock",
    };

    private readonly object _lock = new();
    private DateTime _turnStartUtc;
    private readonly Dictionary<string, string?> _explicit = new(StringComparer.OrdinalIgnoreCase); // register_artifact: absPath -> desc
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);              // write_file/append_file 绝对路径
    private readonly List<string> _mediaUrls = [];
    private string? _taskDirRel;    // 相对工作区的任务目录（如 tasks/20261102-report）

    public ArtifactCollector(Workspace workspace) => _workspace = workspace;

    /// <summary>回合开始：清空采集状态并记录时间基准（mtime 判定用，留 2s 宽容）。</summary>
    public void BeginTurn()
    {
        lock (_lock)
        {
            _turnStartUtc = DateTime.UtcNow.AddSeconds(-2);
            _explicit.Clear();
            _written.Clear();
            _mediaUrls.Clear();
            _taskDirRel = null;
        }
    }

    /// <summary>FileTools 写入回调：登记文件绝对路径（任务目录/散文件判定共用）。</summary>
    public void OnFileWritten(string absolutePath)
    {
        lock (_lock)
        {
            _written.Add(absolutePath);
            TryCaptureTaskDir(absolutePath);
        }
    }

    /// <summary>register_artifact 工具入口：显式登记交付文件与描述。</summary>
    public void RegisterArtifact(string absolutePath, string? description)
    {
        lock (_lock)
        {
            _explicit[absolutePath] = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            TryCaptureTaskDir(absolutePath);
        }
    }

    /// <summary>media_generate 成功回调：登记远程媒体 URL。</summary>
    public void OnMediaUrl(string url)
    {
        lock (_lock)
        {
            if (!_mediaUrls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                _mediaUrls.Add(url);
            }
        }
    }

    /// <summary>工具调用观察：从参数 JSON 中捕获 tasks/&lt;dir&gt;/ 线索。</summary>
    public void ObserveToolCall(string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return;
        lock (_lock) { TryCaptureTaskDir(argsJson); }
    }

    private void TryCaptureTaskDir(string text)
    {
        if (_taskDirRel is not null) return;
        var m = TaskDirRegex.Match(text);
        if (m.Success)
        {
            _taskDirRel = "tasks/" + m.Groups["dir"].Value.TrimEnd('/', '\\');
        }
    }

    /// <summary>回合结束定稿：组装最终产物清单；无产物返回 null。异常静默降级。</summary>
    public TaskArtifacts? Finalize(string taskId)
    {
        try
        {
            lock (_lock)
            {
                var files = new Dictionary<string, ArtifactFile>(StringComparer.OrdinalIgnoreCase);
                var taskDirAbs = _taskDirRel is null ? null : _workspace.ResolveInside(_taskDirRel);

                // 1) deliverables/ 枚举（任务目录专属交付目录）
                var deliverables = taskDirAbs is null ? null : Path.Combine(taskDirAbs, "deliverables");
                if (deliverables is not null && Directory.Exists(deliverables))
                {
                    foreach (var f in Directory.EnumerateFiles(deliverables))
                    {
                        if (IsDeliverable(f)) AddFile(files, f);
                    }
                }

                // 2) 显式登记（register_artifact）：即使文件已被移动也保留条目，UI 标不可用
                foreach (var (path, desc) in _explicit)
                {
                    if (IsDeliverable(path))
                    {
                        files[path] = BuildFile(path, desc);
                    }
                }

                // 3) write_file/append_file 落进 deliverables 的文件（未显式登记的兜底）
                foreach (var p in _written)
                {
                    if (IsDeliverable(p) && deliverables is not null &&
                        p.StartsWith(deliverables + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        AddFile(files, p);
                    }
                }

                // 4) 散文件兜底：任务目录顶层（deliverables 外）或工作区根目录的本回合新文件
                var scanDir = taskDirAbs ?? _workspace.Root;
                var scanRootOnly = taskDirAbs is null;   // 有任务目录时只扫其顶层，无任务目录扫工作区根顶层
                if (Directory.Exists(scanDir))
                {
                    foreach (var f in Directory.EnumerateFiles(scanDir))
                    {
                        if (!IsDeliverable(f)) continue;
                        if (File.GetLastWriteTimeUtc(f) >= _turnStartUtc || _written.Contains(f))
                        {
                            AddFile(files, f);
                        }
                    }
                }
                // 工作区根配置/系统文件永不进产物
                if (scanRootOnly)
                {
                    foreach (var junk in files.Keys.Where(k =>
                        ExcludedRootFiles.Contains(Path.GetFileName(k)) &&
                        string.Equals(Path.GetDirectoryName(k), _workspace.Root, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        files.Remove(junk);
                    }
                }

                // 5) 远程媒体 URL
                foreach (var url in _mediaUrls)
                {
                    var name = UrlFileName(url);
                    files[url] = new ArtifactFile(name, url, null, "link", "link", 0);
                }

                if (files.Count == 0) return null;
                var workDir = taskDirAbs ?? _workspace.Root;
                return new TaskArtifacts(taskId, workDir, files.Values.ToList());
            }
        }
        catch
        {
            return null;   // 采集失败静默降级：产物是显示增强，绝不阻断回合
        }
    }

    private void AddFile(Dictionary<string, ArtifactFile> files, string path)
    {
        if (files.ContainsKey(path)) return;
        var desc = _explicit.TryGetValue(path, out var d) ? d : null;
        files[path] = BuildFile(path, desc);
    }

    private static ArtifactFile BuildFile(string path, string? desc)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        long size = 0;
        try { size = File.Exists(path) ? new FileInfo(path).Length : 0; } catch { }
        return new ArtifactFile(name, path, desc ?? GuessDescription(name, ext), ext, "file", size);
    }

    /// <summary>是否可交付：非隐藏、非垃圾扩展、不在系统目录内。</summary>
    private bool IsDeliverable(string absPath)
    {
        var name = Path.GetFileName(absPath);
        if (name.StartsWith('.')) return false;
        if (JunkExtensions.Contains(Path.GetExtension(absPath))) return false;
        var rel = Path.GetRelativePath(_workspace.Root, absPath);
        if (rel.StartsWith("..")) return false;   // 工作区外不追踪
        var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return !ExcludedTopDirs.Contains(top.ToLowerInvariant());
    }

    /// <summary>规则化短描述（无 LLM）：扩展名语义 + 文件名。</summary>
    private static string GuessDescription(string name, string ext) => ext switch
    {
        "docx" or "doc" or "odt" or "rtf" => "文档",
        "xlsx" or "xls" or "csv" => "数据表格",
        "pptx" or "ppt" => "演示文稿",
        "pdf" => "PDF 文档",
        "md" or "txt" => "文本文档",
        "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "svg" => "图片",
        "mp4" or "mov" or "webm" or "mkv" or "avi" => "视频",
        "mp3" or "wav" or "m4a" or "flac" or "ogg" => "音频",
        "zip" or "rar" or "7z" or "tar" or "gz" => "压缩包",
        "html" or "htm" => "网页文件",
        "json" or "xml" or "yaml" or "yml" => "结构化数据",
        "py" or "js" or "ts" or "cs" or "java" or "go" or "rs" or "sh" or "ps1" => "代码/脚本",
        "exe" or "msi" or "dmg" or "apk" => "可执行程序",
        _ => "任务产出文件",
    };

    private static string UrlFileName(string url)
    {
        try
        {
            var seg = new Uri(url).Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrEmpty(seg)) return Uri.UnescapeDataString(seg);
        }
        catch { }
        return url;
    }
}
