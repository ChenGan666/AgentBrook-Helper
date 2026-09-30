using System.Text.RegularExpressions;

namespace AgentBrook.Agent.Skills;

/// <summary>一个已发现的技能：skills/&lt;name&gt;/SKILL.md。</summary>
public sealed record SkillInfo(
    string Name,
    string Description,
    string SkillDirectory,
    string SkillFilePath)
{
    /// <summary>技能目录内除 SKILL.md 之外的支撑文件（脚本、模板等），相对工作区。</summary>
    public IReadOnlyList<string> SupportingFiles { get; private set; } = [];

    public static SkillInfo From(string skillDir)
    {
        var skillFilePath = Path.Combine(skillDir, "SKILL.md");
        var (name, description) = ParseFrontmatter(skillFilePath);
        name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(skillDir) : name;
        return new SkillInfo(name, description, skillDir, skillFilePath);
    }

    /// <summary>刷新支撑文件列表。</summary>
    public SkillInfo WithSupportingFiles(string workspaceRoot)
    {
        SupportingFiles = System.IO.Directory.Exists(SkillDirectory)
            ? System.IO.Directory.EnumerateFiles(SkillDirectory, "*", SearchOption.AllDirectories)
                .Where(f => !f.Equals(SkillFilePath, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(workspaceRoot, f).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        return this;
    }

    /// <summary>解析 SKILL.md 顶部 YAML frontmatter 的 name / description 两个字段。</summary>
    private static (string? Name, string Description) ParseFrontmatter(string path)
    {
        if (!File.Exists(path))
        {
            return (null, "");
        }

        try
        {
            var lines = File.ReadLines(path).Take(50).ToList();
            if (lines.FirstOrDefault(l => l.Trim() == "---") is null)
            {
                return (null, "");
            }

            string? name = null, description = null;
            var inBlock = false;
            foreach (var line in lines)
            {
                if (line.Trim() == "---")
                {
                    if (!inBlock) { inBlock = true; continue; }
                    break;
                }
                if (!inBlock) continue;

                var m = Regex.Match(line, @"^(name|description)\s*:\s*(.+)$");
                if (!m.Success) continue;
                if (m.Groups[1].Value == "name") name = m.Groups[2].Value.Trim();
                else description = m.Groups[2].Value.Trim();
            }
            return (name, description ?? "");
        }
        catch (Exception)
        {
            return (null, "");
        }
    }
}

/// <summary>扫描技能目录，构建可用技能清单。</summary>
public static class SkillRegistry
{
    public static IReadOnlyList<SkillInfo> Load(string skillsDir, string workspaceRoot)
    {
        if (!Directory.Exists(skillsDir))
        {
            return [];
        }

        return Directory.GetDirectories(skillsDir)
            .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
            .Select(SkillInfo.From)
            .Select(s => s.WithSupportingFiles(workspaceRoot))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>生成注入系统指令的技能摘要（只含元数据，正文按需加载）。</summary>
    public static string BuildSummary(IReadOnlyList<SkillInfo> skills)
    {
        if (skills.Count == 0)
        {
            return "## 可用技能\n（当前没有已安装的技能。技能放置于 skills/<name>/SKILL.md 后可用。）";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 可用技能（Skills）");
        sb.AppendLine("以下是已安装技能的清单。当任务与某技能相关时，先调用 load_skill 工具加载其完整指令再执行：");
        sb.AppendLine();
        foreach (var s in skills)
        {
            var extra = s.SupportingFiles.Count > 0 ? $"（含 {s.SupportingFiles.Count} 个支撑文件）" : "";
            sb.AppendLine($"- {s.Name}: {s.Description}{extra}");
        }
        return sb.ToString().TrimEnd();
    }
}
