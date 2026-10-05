using System.Text;
using AgentBrook.Agent.Mcp;

namespace AgentBrook.Agent.Skills;

/// <summary>
/// 主能力索引：把已装技能、MCP 工具、内置工具分组整合为一个自动生成的主技能
/// （skills/_master/SKILL.md）。启动时与能力变化（安装技能/接入 MCP）后自动重建，
/// 保证索引与实际能力一致。模型 load_skill("_master") 可取得完整能力地图。
/// </summary>
public static class MasterIndex
{
    public const string SkillName = "_master";

    /// <summary>重建 skills/_master/SKILL.md。</summary>
    public static void Rebuild(string skillsDir,
        IReadOnlyList<SkillInfo> skills,
        McpGateTools? mcp,
        string builtinOverview)
    {
        try
        {
            var dir = Path.Combine(skillsDir, SkillName);
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"name: {SkillName}");
            sb.AppendLine("description: 主能力索引（自动生成，勿手改）：全部技能、MCP 工具与内置工具的总目录。规划任务或找不到能力时先读它。");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# 能力主索引（{DateTime.Now:yyyy-MM-dd HH:mm} 自动更新）");
            sb.AppendLine();
            sb.AppendLine("## 内置工具（直接调用）");
            sb.AppendLine(builtinOverview);
            sb.AppendLine();
            sb.AppendLine("## 已装技能（load_skill 加载完整手册）");
            if (skills.Count == 0)
            {
                sb.AppendLine("（无）");
            }
            foreach (var s in skills.Where(s => s.Name != SkillName))
            {
                var files = s.SupportingFiles.Count > 0 ? $"｜支撑文件 {s.SupportingFiles.Count} 个" : "";
                sb.AppendLine($"- {s.Name}：{s.Description}{files}");
            }
            sb.AppendLine();
            sb.AppendLine("## MCP 外部工具（mcp_call 调用，mcp_tool_help 查参数）");
            if (mcp is null || mcp.ToolCount == 0)
            {
                sb.AppendLine("（未接入）");
            }
            else
            {
                sb.AppendLine(GenerateMcpSection(mcp));
            }

            File.WriteAllText(Path.Combine(dir, "SKILL.md"), sb.ToString());
        }
        catch
        {
            // 索引生成失败不影响主流程（下次启动再试）
        }
    }

    private static string GenerateMcpSection(McpGateTools mcp)
    {
        var block = mcp.BuildCatalogBlock();
        var lines = block.Split('\n');
        // 去掉块头两行说明，只保留工具行
        return string.Join('\n', lines.SkipWhile(l =>
            l.StartsWith("##") || l.StartsWith("调用方式") || l.Trim().Length == 0));
    }
}
