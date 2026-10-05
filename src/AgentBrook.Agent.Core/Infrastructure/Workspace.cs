namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 集中管理应用根目录与工作区内的受控目录（memory / skills / session 等）。
/// 所有文件类工具都以 <see cref="Root"/> 为沙箱边界。
/// </summary>
public sealed class Workspace
{
    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        MemoryDir = EnsureDir(Path.Combine(Root, "memory"));
        SkillsDir = EnsureDir(Path.Combine(Root, "skills"));
        SessionsDir = EnsureDir(Path.Combine(Root, "sessions"));
        SessionFile = Path.Combine(Root, "session.json");
        SessionSummaryFile = Path.Combine(Root, "session-summary.txt");
        ProvidersFile = Path.Combine(Root, "models.json");
    }

    /// <summary>工作区根目录（文件读写与命令执行的沙箱边界）。</summary>
    public string Root { get; }

    /// <summary>Markdown 记忆目录。</summary>
    public string MemoryDir { get; }

    /// <summary>技能目录：skills/&lt;name&gt;/SKILL.md。</summary>
    public string SkillsDir { get; }

    /// <summary>多会话存档目录：sessions/&lt;id&gt;/。</summary>
    public string SessionsDir { get; }

    /// <summary>会话持久化文件。</summary>
    public string SessionFile { get; }

    /// <summary>会话滚动摘要文件（与 SessionFile 配对，记录被压缩掉的早期对话）。</summary>
    public string SessionSummaryFile { get; }

    /// <summary>模型供应商配置（用户可管理，独立于 appsettings）。</summary>
    public string ProvidersFile { get; }

    /// <summary>把相对路径解析到工作区内；越界（路径穿越）时抛异常。</summary>
    public string ResolveInside(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        var normalizedRoot = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"路径越出工作区沙箱：{relativePath}");
        }
        return full;
    }

    /// <summary>定位应用根目录：从程序目录向上找包含 csproj 的目录；找不到则回退到当前工作目录。</summary>
    public static string LocateAppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.GetFiles(dir.FullName, "*.csproj").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>把配置值中的 {WORKSPACE} 占位符替换为工作区绝对路径。</summary>
    public string Expand(string value) => value.Replace("{WORKSPACE}", Root);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
