namespace AgentBrook.Agent.Infrastructure;

/// <summary>控制台输出小助手（统一配色与提示符）。</summary>
public static class Ui
{
    public static void Line(string text) => Console.WriteLine(text);

    public static void Dim(string text) => Console.WriteLine($"\u001b[2m{text}\u001b[0m");

    public static void Warn(string text) => Console.WriteLine($"\u001b[33m{text}\u001b[0m");

    /// <summary>审批/提问等需要用户注意的交互提示。</summary>
    public static void Prompt(string text) => Console.WriteLine($"\u001b[35m{text}\u001b[0m");

    public static void AgentHeader(string name, string model) =>
        Console.WriteLine($"\u001b[36m{name}\u001b[2m（{model}）\u001b[0m");
}
