using Avalonia;
using System;

namespace AgentBrook.Helper;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--mdtest"))
        {
            RunMarkdownSelfTest();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Markdown 渲染自检：统计控件树规模并输出到 /tmp/mdtest_result.txt。</summary>
    private static void RunMarkdownSelfTest()
    {
        var md = "## 标题\n\n段落一 **粗体** 与 `code`。\n\n- 项目 A\n- 项目 B\n\n| 列1 | 列2 |\n|---|---|\n| a | b |\n\n```\ncode block\n```\n";
        var control = MarkdownView.Render(md);
        var sb = new System.Text.StringBuilder();
        void Dump(Avalonia.LogicalTree.ILogical node, int depth)
        {
            sb.AppendLine(new string(' ', depth) + node.GetType().Name);
            foreach (var child in node.LogicalChildren) { Dump(child, depth + 1); }
        }
        Dump((Avalonia.LogicalTree.ILogical)control, 0);
        System.IO.File.WriteAllText("/tmp/mdtest_result.txt",
            $"顶层子控件数={(control is Avalonia.Controls.Panel p ? p.Children.Count : -1)}\n" + sb);
        Console.WriteLine("mdtest done → /tmp/mdtest_result.txt");
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Avalonia.AvaloniaNativePlatformOptions
            {
                RenderingMode = new[] { Avalonia.AvaloniaNativeRenderingMode.Software },
            })
            .LogToTrace();
}
