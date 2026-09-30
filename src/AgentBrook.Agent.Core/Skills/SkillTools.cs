using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Skills;

/// <summary>模型可见的技能工具：实时清单、按需加载、自我扩展（创建/安装技能）、技能市场。</summary>
public sealed class SkillTools(SkillStore store, string marketUrl = "")
{
    private readonly SkillStore _store = store;
    private readonly string _marketUrl = marketUrl;

    [Description("列出所有已安装技能的名称与描述（实时清单，创建/修改后立即生效）。正文需用 load_skill 加载。")]
    public string list_skills()
    {
        var skills = _store.Current;
        if (skills.Count == 0)
        {
            return "（当前没有已安装的技能。可用 create_skill 创建，或放置 skills/<name>/SKILL.md）";
        }

        var sb = new StringBuilder("已安装技能：\n");
        foreach (var s in skills)
        {
            sb.AppendLine($"- {s.Name}: {s.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("加载指定技能的完整指令（SKILL.md 正文）。执行相关任务前应先加载。")]
    public string load_skill([Description("技能名称")] string name)
    {
        var skill = FindSkill(name);
        if (skill is null)
        {
            return $"未找到技能“{name}”。可用技能：{string.Join("、", _store.Current.Select(s => s.Name))}";
        }

        var content = File.ReadAllText(skill.SkillFilePath);
        var sb = new StringBuilder(content.TrimEnd());
        if (skill.SupportingFiles.Count > 0)
        {
            sb.AppendLine().AppendLine()
              .AppendLine("本技能附带的支撑文件（可用 read_file 读取）：");
            foreach (var f in skill.SupportingFiles)
            {
                sb.AppendLine($"- {f}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 自我扩展：把当前任务沉淀为可复用技能。创建后立即生效（list_skills 可见、下次可直接 load_skill）。
    /// </summary>
    [Description("创建一个新技能并立即生效。当用户要求的能力值得沉淀为可复用流程（操作手册、工作方法、自动化步骤）时使用：把你刚验证过的做法整理成清晰的步骤写入正文。创建后用 list_skills 确认。")]
    public string create_skill(
        [Description("技能名：小写字母/数字/连字符，如 code-review、weekly-report")] string name,
        [Description("一句话描述：什么情况下应使用该技能")] string description,
        [Description("SKILL.md 正文：该技能的完整操作指南（Markdown），应包含触发条件、步骤、注意事项")] string content,
        [Description("可选：技能目录内的支撑文件（相对 skills/<name>/ 的路径与内容），格式为 \"路径|内容\"，多条用 \\n---\\n 分隔")] string? supportingFiles = null)
    {
        var error = _store.CreateSkill(name, description, content, out var created);
        if (error is not null)
        {
            return $"创建失败：{error}";
        }

        // 可选支撑文件
        if (!string.IsNullOrWhiteSpace(supportingFiles))
        {
            var dir = Path.Combine(_store.SkillsDirectory, created!);
            foreach (var part in supportingFiles.Split("\n---\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var sep = part.IndexOf('|');
                if (sep <= 0)
                {
                    continue;
                }
                var rel = part[..sep].Trim();
                var body = part[(sep + 1)..];
                var safeRel = rel.Replace("..", "").TrimStart('/');
                if (safeRel.Length == 0)
                {
                    continue;
                }
                var full = Path.GetFullPath(Path.Combine(dir, safeRel));
                if (!full.StartsWith(Path.GetFullPath(dir), StringComparison.Ordinal))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, body);
            }
        }

        return $"✔ 技能「{created}」已创建并立即生效。现在起：list_skills 可见、load_skill 可加载、后续同类任务请直接使用该技能。";
    }

    /// <summary>
    /// 从 zip 包（URL 或本地路径）安装技能包：下载/解压 → 校验 SKILL.md → 安装到技能目录并立即生效。
    /// 安装远程技能包属于敏感操作，通常需要用户批准。
    /// </summary>
    [Description("安装技能包：从 URL 或本地 zip 文件安装一个技能（包内需含 SKILL.md）。仅安装可信来源的技能包。安装前通常需要用户批准；安装成功后立即生效。")]
    public async Task<string> install_skill(
        [Description("技能包来源：http(s) zip 下载地址，或本地 zip 文件路径")] string source,
        [Description("可选：期望的技能名（不填则自动从 SKILL.md 解析）")] string? expectedName = null)
    {
        var (error, installed) = await _store.InstallFromZipDetailedAsync(source, expectedName);
        if (error is not null)
        {
            return $"安装失败：{error}";
        }
        var desc = _store.Current.FirstOrDefault(s => s.Name == installed)?.Description ?? "";
        return $"✔ 技能「{installed}」安装成功并立即生效（{desc}）。现在可以用 load_skill 加载它。";
    }

    /// <summary>浏览技能市场：从配置的远程索引拉取可用技能包清单，可按关键词过滤。确定要装某个后，用 install_skill 安装其 url。</summary>
    public async Task<string> skill_market([Description("可选：按关键词过滤名称或描述")] string? keyword = null)
    {
        if (string.IsNullOrWhiteSpace(_marketUrl))
        {
            return "未配置技能市场地址（Agent:SkillMarketUrl，指向一个 JSON 索引：{\"skills\":[{\"name\",\"description\",\"url\"}]}）。也可以直接把技能包 zip 的 URL 给 install_skill 安装。";
        }
        var raw = await _store.FetchMarketIndexAsync(_marketUrl);
        if (raw is null)
        {
            return "技能市场不可达，请检查网络或 SkillMarketUrl 配置。";
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var entries = doc.RootElement.GetProperty("skills").EnumerateArray()
                .Select(e => (
                    Name: e.GetProperty("name").GetString() ?? "",
                    Desc: e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Url: e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : ""))
                .Where(x => keyword is null
                    || x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || x.Desc.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count == 0)
            {
                return "技能市场中没有匹配的技能。";
            }
            var sb = new StringBuilder("技能市场可用技能：\n");
            foreach (var x in entries)
            {
                sb.AppendLine($"- {x.Name}: {x.Desc}");
            }
            sb.Append("确定要安装哪个后，用 install_skill 并提供其 url 即可。");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"市场索引解析失败：{ex.Message}";
        }
    }

    private SkillInfo? FindSkill(string name) =>
        _store.Current.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(s.SkillDirectory), name, StringComparison.OrdinalIgnoreCase));
}
