using System.ComponentModel;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Agent.Tools;

/// <summary>产物登记工具：Agent 把最终交付文件放入任务目录 deliverables/ 后调用，登记文件与用途说明。</summary>
public sealed class ArtifactTools(Workspace workspace, ArtifactCollector collector)
{
    private readonly Workspace _workspace = workspace;
    private readonly ArtifactCollector _collector = collector;

    [Description("登记一个最终交付产物文件（path 相对工作区根，通常位于 tasks/<任务目录>/deliverables/ 下；description 为一句话用途说明）。每个交付文件都应调用一次。")]
    public string register_artifact(
        [Description("交付文件相对路径，如 tasks/xxx/deliverables/report.docx")] string path,
        [Description("文件用途的一句话说明，展示给用户")] string description = "")
    {
        try
        {
            var full = _workspace.ResolveInside(path);
            if (!File.Exists(full))
            {
                return $"文件不存在：{path}。请确认交付文件已生成后再登记。";
            }
            _collector.RegisterArtifact(full, description);
            return $"已登记产物：{Path.GetFileName(full)}";
        }
        catch (Exception ex)
        {
            return $"登记失败：{ex.Message}";
        }
    }
}
