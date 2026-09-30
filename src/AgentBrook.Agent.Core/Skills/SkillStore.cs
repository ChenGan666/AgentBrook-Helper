using System.Text;
using System.Text.RegularExpressions;

namespace AgentBrook.Agent.Skills;

/// <summary>
/// 技能仓库：技能清单每次调用实时扫描磁盘（新写入的技能立即可见），
/// 并提供 create_skill 的落盘实现——支撑智能体「自我扩展」能力。
/// </summary>
public sealed class SkillStore
{
    private readonly string _skillsDir;
    private readonly string _workspaceRoot;

    /// <summary>技能根目录。</summary>
    public string SkillsDirectory => _skillsDir;

    public SkillStore(string skillsDir, string workspaceRoot)
    {
        _skillsDir = skillsDir;
        _workspaceRoot = workspaceRoot;
    }

    /// <summary>实时扫描技能清单。</summary>
    public IReadOnlyList<SkillInfo> Current => SkillRegistry.Load(_skillsDir, _workspaceRoot);

    /// <summary>技能名规范化：小写、空格转连字符、剔除非法字符。</summary>
    public static string SanitizeName(string name)
    {
        var cleaned = (name ?? "").Trim().ToLowerInvariant().Replace(' ', '-');
        cleaned = Regex.Replace(cleaned, @"[^a-z0-9\-_\u4e00-\u9fff]", "");
        return cleaned;
    }
    /// <summary>从 zip（URL 或本地路径）安装技能包。返回错误信息或 null；installedName 为安装后的技能名。</summary>
    public async Task<string?> InstallFromZipAsync(string source, string? expectedName = null)
    {
        var (error, _) = await InstallFromZipDetailedAsync(source, expectedName);
        return error;
    }


    /// <summary>安装技能包（详细版）：返回 (错误信息, 安装的技能名)。</summary>
    public async Task<(string? Error, string? InstalledName)> InstallFromZipDetailedAsync(string source, string? expectedName = null)
    {
        try
        {
            return await InstallFromZipDetailedCoreAsync(source, expectedName);
        }
        catch (Exception ex)
        {
            return ($"安装过程出错：{ex.Message}", null);
        }
    }

    private async Task<(string? Error, string? InstalledName)> InstallFromZipDetailedCoreAsync(string source, string? expectedName = null)
    {
        string zipPath;
        var isUrl = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isUrl)
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var bytes = await http.GetByteArrayAsync(source);
            zipPath = Path.Combine(Path.GetTempPath(), "brook_skill_install.zip");
            await File.WriteAllBytesAsync(zipPath, bytes);
        }
        else
        {
            zipPath = Path.GetFullPath(source);
            if (!File.Exists(zipPath))
            {
                return ($"文件不存在：{source}", null);
            }
        }

        // zip 魔数校验（PK\x03\x04）：GitHub 仓库地址等非 zip 来源会下载到网页/错误内容
        var magic = new byte[4];
        using (var fs = File.OpenRead(zipPath))
        {
            if (await fs.ReadAsync(magic) < 4)
            {
                return ("下载内容为空，不是有效的技能包", null);
            }
        }
        if (magic[0] != 0x50 || magic[1] != 0x4B)
        {
            return ("来源不是 zip 技能包（可能是网页或仓库地址）。请提供 zip 直链；若是 GitHub 仓库，可让我先克隆再打包，或提供 release 的 zip 下载地址", null);
        }

        System.IO.Compression.ZipArchive archive;
        try
        {
            archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex)
        {
            return ($"无法读取 zip 包：{ex.Message}", null);
        }

        // 安全扫描：拒绝路径穿越与绝对路径条目
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace("\\", "/");
            if (name.StartsWith("/") || name.Contains(".."))
            {
                return ("技能包含不安全的路径条目，已拒绝安装", null);
            }
        }

        // 定位 SKILL.md（最浅层级优先）
        var skillEntry = archive.Entries
            .Where(e => e.FullName.Replace("\\", "/").EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)
                     || e.FullName.Replace("\\", "/").Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/'))
            .FirstOrDefault();
        if (skillEntry is null)
        {
            return ("包内未找到 SKILL.md（不是有效的技能包）", null);
        }

        var prefix = skillEntry.FullName.Replace("\\", "/");
        prefix = prefix[..(prefix.LastIndexOf('/') + 1)];

        // 解析 frontmatter name/description
        string frontName = "", frontDesc = "";
        using (var sr = new StreamReader(skillEntry.Open()))
        {
            var metaLines = new List<string>();
            string? line;
            while ((line = await sr.ReadLineAsync()) is not null && metaLines.Count < 30)
            {
                metaLines.Add(line);
            }
            var inBlock = false;
            foreach (var l in metaLines)
            {
                if (l.Trim() == "---") { if (!inBlock) { inBlock = true; continue; } break; }
                if (!inBlock) continue;
                var m = Regex.Match(l, "^(name|description)\\s*:\\s*(.+)$");
                if (m.Success)
                {
                    if (m.Groups[1].Value == "name") frontName = m.Groups[2].Value.Trim();
                    else frontDesc = m.Groups[2].Value.Trim();
                }
            }
        }

        var dirName = SanitizeName(expectedName ?? frontName);
        if (dirName.Length < 2)
        {
            dirName = SanitizeName(prefix.TrimEnd('/').Split('/').LastOrDefault() ?? "");
        }
        if (dirName.Length < 2)
        {
            return ("无法确定技能名（frontmatter 缺少 name 且目录名无效）", null);
        }

        var target = Path.Combine(_skillsDir, dirName);
        if (Directory.Exists(target))
        {
            return ($"技能 {dirName} 已存在，未覆盖安装（如需更新请先删除 skills/{dirName}）", null);
        }

        Directory.CreateDirectory(target);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            var entryPath = entry.FullName.Replace("\\", "/");
            if (!entryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.Length == 0)
            {
                continue;
            }
            var rel = entryPath[prefix.Length..];
            if (rel.Length == 0) continue;
            var full = Path.GetFullPath(Path.Combine(target, rel));
            if (!full.StartsWith(Path.GetFullPath(target), StringComparison.Ordinal))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var inStream = entry.Open();
            using var outStream = File.Create(full);
            await inStream.CopyToAsync(outStream);
            count++;
        }

        return (null, dirName);
    }

    /// <summary>拉取技能市场索引（JSON：{"skills":[{"name","description","url"}]}），返回原始文本或 null。</summary>
    public async Task<string?> FetchMarketIndexAsync(string marketUrl)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            return await http.GetStringAsync(marketUrl);
        }
        catch
        {
            return null;
        }
    }


    /// <summary>创建新技能：写入 skills/&lt;name&gt;/SKILL.md（frontmatter + 正文）。返回错误信息或 null。</summary>
    public string? CreateSkill(string name, string description, string content, out string? skillName)
    {
        skillName = null;
        var cleaned = SanitizeName(name);
        if (cleaned.Length < 2)
        {
            return "技能名至少需要 2 个有效字符";
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return "技能内容不能为空";
        }

        var dir = Path.Combine(_skillsDir, cleaned);
        if (Directory.Exists(dir))
        {
            return $"技能 {cleaned} 已存在（如需更新请直接覆盖 SKILL.md 或换个名字）";
        }

        Directory.CreateDirectory(dir);
        description = string.IsNullOrWhiteSpace(description) ? cleaned : description.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {cleaned}");
        sb.AppendLine($"description: {description.Replace('\n', ' ')}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# 技能：{cleaned}");
        sb.AppendLine();
        sb.AppendLine(content.Trim());
        sb.AppendLine();
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), sb.ToString());

        skillName = cleaned;
        return null;
    }
}
