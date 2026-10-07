namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 运行时探测当前操作系统，生成注入系统提示词的环境指令块。
/// 目的：让模型按当前系统自动选择命令语法（Windows 用 cmd/python，macOS 用 zsh/python3），
/// 替代在 appsettings 里写死「当前是 macOS 环境」的静态声明。
/// </summary>
public static class PlatformEnvironment
{
    /// <summary>当前系统的环境指令块（拼接进系统提示词）。</summary>
    public static string DescribeBlock()
    {
        if (OperatingSystem.IsWindows())
        {
            return """
                # 当前运行环境（系统自动探测）
                - 操作系统：Windows。run_command 由 cmd.exe 执行：命令用 Windows 语法，路径用反斜杠。
                - Python 命令是 python（python3 通常不存在）；装包用 python -m pip install。
                - 没有 sed / awk / head / grep / find 等 Unix 工具；文本处理改用 python 或 powershell -Command "<语句>"。
                - 命令失败时先读 stderr 找原因（命令不存在/缺依赖/路径不对）再修正重试；不要因连续失败而放弃任务。
                - 打开文件或网页用 start 后跟目标（首个参数是窗口标题，惯用 start "" <目标> 占位）。
                """;
        }

        if (OperatingSystem.IsMacOS())
        {
            return """
                # 当前运行环境（系统自动探测）
                - 操作系统：macOS。run_command 由 zsh 执行：命令用 Unix 语法，路径用正斜杠。
                - Python 命令是 python3；装包用 python3 -m pip install。
                - 命令失败时先读 stderr 找原因再修正重试；不要因连续失败而放弃任务。
                - 打开文件或网页用 open <目标>。
                """;
        }

        return """
            # 当前运行环境（系统自动探测）
            - 操作系统：Linux。run_command 由 sh/bash 执行：命令用 Unix 语法，路径用正斜杠。
            - Python 命令是 python3；装包用 python3 -m pip install。
            - 命令失败时先读 stderr 找原因再修正重试；不要因连续失败而放弃任务。
            - 打开文件或网页用 xdg-open <目标>。
            """;
    }
}
