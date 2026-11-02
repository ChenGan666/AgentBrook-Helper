using AgentBrook.Agent.Infrastructure;
using AgentBrook.Helper.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentBrook.Helper.Views;

/// <summary>
/// 任务产物展示卡：助手最终回复底部的独立区域。
/// 内容 = 任务工作目录行（可点击打开）+ 最终交付文件清单（文件/远程链接，可点击打开）。
/// 实时消息与历史回放共用：Build() 返回 null 表示无产物不渲染，零布局占位。
/// </summary>
public static class ArtifactPanel
{
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.Parse("#3a3a3a"));
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#1f1f1f"));
    private static readonly IBrush TextMain = new SolidColorBrush(Color.Parse("#e6e6e6"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#9a9a9a"));
    private static readonly IBrush TextLink = new SolidColorBrush(Color.Parse("#6cb6ff"));
    private static readonly IBrush TextBad = new SolidColorBrush(Color.Parse("#d08080"));

    /// <summary>构造产物卡；artifacts 为空或无文件返回 null（不渲染）。</summary>
    public static Control? Build(TaskArtifacts? artifacts)
    {
        if (artifacts is null || !artifacts.HasFiles)
        {
            return null;
        }

        var root = new StackPanel { Spacing = 6 };

        // 标题行：图标 + "任务产物" + 文件计数
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock { Text = "📦", FontSize = 14 });
        header.Children.Add(new TextBlock
        {
            Text = I18n.T("ui.artifacts.title"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextMain,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"({artifacts.Files.Count})",
            FontSize = 12,
            Foreground = TextDim,
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(header);

        // 工作目录行
        root.Children.Add(BuildDirectoryRow(artifacts.WorkingDirectory));

        // 文件清单（高度受限 + 滚动，不撑开对话区）
        var list = new StackPanel { Spacing = 2 };
        foreach (var f in artifacts.Files)
        {
            list.Children.Add(f.Kind == "link" ? BuildLinkRow(f) : BuildFileRow(f));
        }
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 180,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = list,
        });

        return new Border
        {
            Background = CardBg,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,   // 卡片吃满气泡可用宽度
            Child = root,
        };
    }

    /// <summary>整行可点击的默认按钮：内容左对齐、行宽随容器拉伸。</summary>
    private static Button RowButton(Control content, Action onClick)
    {
        var btn = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Control BuildDirectoryRow(string dir)
    {
        var exists = Directory.Exists(dir);
        var row = RowGrid();
        row.Children.Add(new TextBlock { Text = "📁", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock
        {
            Text = exists ? dir : $"{dir}（{I18n.T("ui.artifacts.unavailable")}）",
            FontSize = 12.5,
            Foreground = exists ? TextLink : TextBad,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        ToolTip.SetTip(name, dir);
        row.Children.Add(name);
        return exists ? RowButton(row, () => MarkdownView.OpenTarget(dir)) : row;
    }

    private static Control BuildFileRow(ArtifactFile f)
    {
        var exists = File.Exists(f.FilePath);
        var row = RowGrid();

        row.Children.Add(new TextBlock { Text = IconFor(f.FileType), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock
        {
            Text = f.FileName,
            FontSize = 12.5,
            Foreground = exists ? TextLink : TextBad,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        ToolTip.SetTip(name, f.FilePath);
        row.Children.Add(name);

        if (!exists)
        {
            var bad = new TextBlock
            {
                Text = I18n.T("ui.artifacts.unavailable"),
                FontSize = 11.5,
                Foreground = TextBad,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(bad, 2);
            row.Children.Add(bad);
            return row;   // 不可点击：明确不可用态
        }

        var desc = f.Description ?? "";
        if (f.Size > 0)
        {
            desc = desc.Length > 0 ? $"{desc} · {FormatSize(f.Size)}" : FormatSize(f.Size);
        }
        if (desc.Length > 0)
        {
            var descTb = new TextBlock
            {
                Text = desc,
                FontSize = 11.5,
                Foreground = TextDim,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(descTb, 2);
            row.Children.Add(descTb);
        }
        // 点击时再查存在性无意义（OpenTarget 对不存在目标静默兜底），直接打开
        return RowButton(row, () => MarkdownView.OpenTarget(f.FilePath));
    }

    private static Control BuildLinkRow(ArtifactFile f)
    {
        var row = RowGrid();
        row.Children.Add(new TextBlock { Text = "🌐", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock
        {
            Text = f.FileName,
            FontSize = 12.5,
            Foreground = TextLink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        ToolTip.SetTip(name, f.FilePath);
        row.Children.Add(name);
        var descTb = new TextBlock
        {
            Text = f.Description ?? I18n.T("ui.artifacts.remote"),
            FontSize = 11.5,
            Foreground = TextDim,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(descTb, 2);
        row.Children.Add(descTb);
        return RowButton(row, () => MarkdownView.OpenTarget(f.FilePath));
    }

    /// <summary>行布局：图标 Auto | 主文本 *（截断）| 辅助文本 Auto，随容器宽度自适应。</summary>
    private static Grid RowGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        ColumnSpacing = 6,
    };

    private static string IconFor(string ext) => ext switch
    {
        "docx" or "doc" or "odt" or "rtf" => "📄",
        "xlsx" or "xls" or "csv" => "📊",
        "pptx" or "ppt" => "📽️",
        "pdf" => "📕",
        "md" or "txt" => "📝",
        "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "svg" => "🖼️",
        "mp4" or "mov" or "webm" or "mkv" or "avi" => "🎬",
        "mp3" or "wav" or "m4a" or "flac" or "ogg" => "🎵",
        "zip" or "rar" or "7z" or "tar" or "gz" => "🗜️",
        "html" or "htm" => "🌐",
        "json" or "xml" or "yaml" or "yml" => "🧾",
        "py" or "js" or "ts" or "cs" or "java" or "go" or "rs" or "sh" or "ps1" => "💻",
        "exe" or "msi" or "dmg" or "apk" => "⚙️",
        _ => "📄",
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>链式配置小工具（Builder 场景少一层嵌套）。</summary>
internal static class ControlExtensions
{
    public static T Also<T>(this T self, Action<T> action) where T : Control
    {
        action(self);
        return self;
    }
}
