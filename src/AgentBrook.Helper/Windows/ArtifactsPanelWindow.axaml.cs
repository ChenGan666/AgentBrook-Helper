using AgentBrook.Agent.Infrastructure;
using AgentBrook.Helper.Support;
using AgentBrook.Helper.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentBrook.Helper.Windows;

/// <summary>
/// 右侧「任务产物」独立浮窗（主窗口外扩展区，主窗口本体零改动）：
/// 无边框、不抢焦点、不进任务栏；由 MainWindow.Artifacts.cs 负责定位（贴主窗口右缘、同高跟随）与显隐联动。
/// 内容构建与产物卡同构：任务目录分组（可折叠，最新在前）→ 目录行（开文件夹）→ 文件行（开文件）。
/// </summary>
public partial class ArtifactsPanelWindow : Window
{
    /// <summary>面板固定宽度（DIP；定位换算与 XAML Width 保持一致）。</summary>
    public const double PanelWidth = 310;

    /// <summary>层叠量（物理像素）：面板左缘压进主窗口客户区右缘的深度，衬在主窗口圆角后方。</summary>
    public const double OverlapPx = 10;

    private static readonly IBrush PanelTextMain = new SolidColorBrush(Color.Parse("#e6e6e6"));
    private static readonly IBrush PanelTextDim = new SolidColorBrush(Color.Parse("#9a9a9a"));
    private static readonly IBrush PanelTextLink = new SolidColorBrush(Color.Parse("#6cb6ff"));
    private static readonly IBrush PanelTextBad = new SolidColorBrush(Color.Parse("#d08080"));

    /// <summary>用户点击头部关闭按钮：宿主据此灭灯顶栏按钮并持久化开合状态。</summary>
    public event Action? CloseRequested;

    public ArtifactsPanelWindow()
    {
        InitializeComponent();
        var closeBtn = this.FindControl<Button>("CloseBtn");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => CloseRequested?.Invoke();
        }
    }

    /// <summary>按聚合产物全量重建面板（标题/计数/空态一并刷新，语言切换复用同一路径）。</summary>
    public void Refresh(IReadOnlyList<TaskArtifacts> groups)
    {
        var title = this.FindControl<TextBlock>("PanelTitle");
        var count = this.FindControl<TextBlock>("PanelCount");
        var empty = this.FindControl<TextBlock>("PanelEmpty");
        var list = this.FindControl<StackPanel>("PanelList");
        var total = groups.Sum(g => g.Files.Count);
        if (title is not null) title.Text = I18n.T("ui.artifacts.title");
        if (count is not null) count.Text = total > 0 ? $"({total})" : "";
        if (empty is not null)
        {
            empty.Text = I18n.T("ui.artifacts.panel.empty");
            empty.IsVisible = total == 0;
        }
        if (list is null) return;
        list.Children.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            list.Children.Add(BuildArtifactsGroup(groups[i], expanded: i == 0));   // 最新任务组默认展开
        }
    }

    /// <summary>任务目录组：头（chevron + 📁 目录短名 + 文件数，点击折叠/展开）+ 内容（目录行 + 文件行）。</summary>
    private static Control BuildArtifactsGroup(TaskArtifacts g, bool expanded)
    {
        var content = new StackPanel { Spacing = 2, Margin = new Thickness(12, 2, 0, 0), IsVisible = expanded };
        content.Children.Add(BuildPanelDirectoryRow(g.WorkingDirectory));
        foreach (var f in g.Files)
        {
            content.Children.Add(f.Kind == "link" ? BuildPanelLinkRow(f) : BuildPanelFileRow(f));
        }

        var chevron = new PathIcon
        {
            Data = (StreamGeometry)Application.Current!.Resources["Icon.ChevronDown"]!,
            Width = 12, Height = 12,
            Foreground = PanelTextDim,
        };
        SetChevron(chevron, expanded);
        var name = Path.GetFileName(g.WorkingDirectory.TrimEnd('/', '\\'));
        var header = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 6 };
        headerGrid.Children.Add(chevron);
        headerGrid.Children.Add(new TextBlock { Text = "📁", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var nameTb = new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            FontWeight = FontWeight.Medium,
            Foreground = PanelTextMain,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameTb, 1);
        ToolTip.SetTip(nameTb, g.WorkingDirectory);
        headerGrid.Children.Add(nameTb);
        var countTb = new TextBlock
        {
            Text = g.Files.Count.ToString(),
            FontSize = 11.5,
            Foreground = PanelTextDim,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countTb, 3);
        headerGrid.Children.Add(countTb);
        header.Content = headerGrid;
        header.Click += (_, _) =>
        {
            var next = !content.IsVisible;
            content.IsVisible = next;
            SetChevron(chevron, next);
        };

        return new StackPanel { Spacing = 2, Children = { header, content } };
    }

    private static void SetChevron(PathIcon chevron, bool expanded) =>
        chevron.RenderTransform = new RotateTransform(expanded ? 0 : -90);   // 展开朝下 / 收起朝右

    /// <summary>目录行：完整路径可点击打开文件夹（缺失时红字不可点）。</summary>
    private static Control BuildPanelDirectoryRow(string dir)
    {
        var exists = Directory.Exists(dir);
        var row = PanelRowGrid();
        row.Children.Add(new TextBlock { Text = "📂", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var path = new TextBlock
        {
            Text = exists ? dir : $"{dir}（{I18n.T("ui.artifacts.unavailable")}）",
            FontSize = 12,
            Foreground = exists ? PanelTextLink : PanelTextBad,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(path, 1);
        ToolTip.SetTip(path, exists ? $"{dir} · {I18n.T("ui.artifacts.panel.openfolder")}" : dir);
        row.Children.Add(path);
        return exists ? PanelRowButton(row, () => MarkdownView.OpenTarget(dir)) : row;
    }

    /// <summary>文件行：emoji 类型图标 + 文件名 + 右侧「类型 · 大小」，点击打开；缺失时红字不可点。</summary>
    private static Control BuildPanelFileRow(ArtifactFile f)
    {
        var exists = File.Exists(f.FilePath);
        var row = PanelRowGrid();
        row.Children.Add(new TextBlock { Text = PanelIconFor(f.FileType), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock
        {
            Text = f.FileName,
            FontSize = 12,
            Foreground = exists ? PanelTextLink : PanelTextBad,
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
                FontSize = 11,
                Foreground = PanelTextBad,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(bad, 2);
            row.Children.Add(bad);
            return row;   // 不可点击：明确不可用态
        }
        var meta = f.Description ?? "";
        if (f.Size > 0)
        {
            meta = meta.Length > 0 ? $"{meta} · {PanelFormatSize(f.Size)}" : PanelFormatSize(f.Size);
        }
        if (meta.Length > 0)
        {
            var metaTb = new TextBlock
            {
                Text = meta,
                FontSize = 11,
                Foreground = PanelTextDim,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(metaTb, 2);
            row.Children.Add(metaTb);
        }
        return PanelRowButton(row, () => MarkdownView.OpenTarget(f.FilePath));
    }

    /// <summary>远程链接行（media_generate 产物）：🌐 + 名称 + 描述/URL，点击打开。</summary>
    private static Control BuildPanelLinkRow(ArtifactFile f)
    {
        var row = PanelRowGrid();
        row.Children.Add(new TextBlock { Text = "🌐", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock
        {
            Text = f.FileName,
            FontSize = 12,
            Foreground = PanelTextLink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        ToolTip.SetTip(name, f.FilePath);
        row.Children.Add(name);
        var metaTb = new TextBlock
        {
            Text = f.Description ?? I18n.T("ui.artifacts.remote"),
            FontSize = 11,
            Foreground = PanelTextDim,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(metaTb, 2);
        row.Children.Add(metaTb);
        return PanelRowButton(row, () => MarkdownView.OpenTarget(f.FilePath));
    }

    /// <summary>行布局：图标 Auto | 主文本 *（截断）| 辅助文本 Auto（与产物卡 RowGrid 同构）。</summary>
    private static Grid PanelRowGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        ColumnSpacing = 6,
    };

    /// <summary>整行可点击的默认按钮：内容左对齐、行宽随面板拉伸（与产物卡 RowButton 同构）。</summary>
    private static Button PanelRowButton(Control content, Action onClick)
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

    /// <summary>扩展名 → emoji 图标（与产物卡 IconFor 同表，就地复制避免改既有文件）。</summary>
    private static string PanelIconFor(string ext) => ext switch
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

    /// <summary>字节 → 人类可读大小（与产物卡 FormatSize 同式，就地复制避免改既有文件）。</summary>
    private static string PanelFormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}
