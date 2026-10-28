using AgentBrook.Helper.Support;
using AgentBrook.Helper.Windows;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using System.Diagnostics;

namespace AgentBrook.Helper.EnvironmentCheck;

/// <summary>单项检测状态。</summary>
public enum CheckStatus
{
    Unknown,
    Ok,
    Warning,
    Error,
}

/// <summary>单项环境检测结果。</summary>
public sealed class EnvironmentCheckResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public CheckStatus Status { get; set; }
    public string Message { get; set; } = "";

    /// <summary>修复后建议的解决文案。</summary>
    public string? Solution { get; set; }

    /// <summary>额外信息（如 MCP 启动命令）。</summary>
    public string? Extra { get; set; }

    /// <summary>是否可以一键修复。</summary>
    public bool CanRepair { get; set; }

    public EnvironmentCheckResult Clone() => new()
    {
        Id = Id,
        Title = Title,
        Status = Status,
        Message = Message,
        Solution = Solution,
        Extra = Extra,
        CanRepair = CanRepair,
    };
}
