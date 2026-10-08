using System.Text;
using Avalonia.Input;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;

namespace AgentBrook.Helper;

/// <summary>
/// 轻量 Markdown → Avalonia 控件渲染器（Markdig AST 驱动，暗色主题友好）。
/// 支持：标题/段落/粗斜体/行内代码/链接文本/列表/围栏代码块/表格/引用/分隔线/本地图片。
/// </summary>
public static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .UseTaskLists()
        .UseEmphasisExtras()
        .Build();

    private static readonly FontFamily UiFont = new("PingFang SC, Hiragino Sans GB, Microsoft YaHei");
    private static readonly FontFamily MonoFont = new("Menlo, monospace");

    public static Control Render(string markdown)
    {
        var doc = Markdown.Parse(markdown ?? "", Pipeline);
        var panel = new StackPanel { Spacing = 6 };
        var textBlock = NewTextBlock();
        var hasText = false;

        void FlushText()
        {
            if (!hasText) return;
            // 移除末尾多余的换行
            var inlines = textBlock.Inlines!;
            while (inlines.Count > 0 && inlines[^1] is LineBreak)
            {
                inlines.RemoveAt(inlines.Count - 1);
            }
            panel.Children.Add(Selectable(textBlock));
            textBlock = NewTextBlock();
            hasText = false;
        }

        foreach (var block in doc)
        {
            switch (block)
            {
                case HeadingBlock h:
                    hasText = true;
                    textBlock.Inlines!.Add(new Run
                    {
                        Text = GetInlineText(h.Inline),
                        FontSize = h.Level switch { 1 => 22, 2 => 19, 3 => 17, _ => 16 },
                        FontWeight = FontWeight.Bold,
                        Foreground = BrushLight,
                    });
                    textBlock.Inlines!.Add(new LineBreak());
                    break;

                case ParagraphBlock p:
                    // 仅含一张图片的段落：作为独立图片控件渲染，不混入文本流
                    if (TryExtractImageOnly(p) is { } imgControl)
                    {
                        FlushText();
                        panel.Children.Add(imgControl);
                        break;
                    }
                    hasText = true;
                    if (p.Inline is not null)
                    {
                        FillInlines(p.Inline, textBlock.Inlines!);
                    }
                    textBlock.Inlines!.Add(new LineBreak());
                    break;

                default:
                    FlushText();
                    var c = RenderBlock(block);
                    if (c is not null)
                    {
                        panel.Children.Add(c);
                    }
                    break;
            }
        }
        FlushText();
        return panel;
    }

    private static SelectableTextBlock NewTextBlock() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = BrushLight,
        FontSize = 15,
    };

    private static Control? RenderBlock(Block block) => block switch
    {
        Markdig.Syntax.FencedCodeBlock fenced => RenderCodeBlock(fenced),
        Markdig.Syntax.CodeBlock code => RenderCodeBlock(code),
        ListBlock list => RenderList(list),
        QuoteBlock quote => RenderQuote(quote),
        Table table => RenderTable(table),
        ThematicBreakBlock => new Separator { Margin = new Thickness(0, 4), Background = new SolidColorBrush(Color.Parse("#444444")) },
        _ => RenderPlainText(block),
    };

    /// <summary>段落仅含一张图片时：块级渲染本地图片；否则返回 null。</summary>
    private static Control? TryExtractImageOnly(ParagraphBlock p)
    {
        if (p.Inline is null) return null;
        var img = p.Inline.FirstOrDefault() as LinkInline;
        if (img is not { IsImage: true } || img.FirstChild is not LiteralInline li) return null;

        var src = li.Content.ToString();
        var full = src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? src
            : Path.Combine(App.Controller.Agent!.WorkspaceRoot, src.TrimStart('/'));
        if (File.Exists(full))
        {
            var preview = new Image
            {
                Source = new Bitmap(full),
                MaxWidth = 380,
                Margin = new Thickness(0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            preview.PointerReleased += (_, _) => OpenTarget(full);
            ToolTip.SetTip(preview, "点击打开原图");
            return preview;
        }
        return new TextBlock
        {
            Text = $"[图片：{src}（文件不存在）]",
            Classes = { "procLine" },
        };
    }

    /// <summary>可选中可复制的文本块：附右键"复制"菜单（有选区复制选区，否则复制全文）。供对话区各文本块复用。</summary>
    public static T Selectable<T>(T tb) where T : SelectableTextBlock
    {
        // 背景必须非 null 才能被鼠标命中，否则只能点击到文字本身、行间空白无法开始选择。
        tb.Background ??= Brushes.Transparent;
        var item = new MenuItem { Header = I18n.T("复制") };
        item.Click += async (_, _) =>
        {
            var text = string.IsNullOrEmpty(tb.SelectedText) ? tb.Text ?? "" : tb.SelectedText;
            var clip = TopLevel.GetTopLevel(tb)?.Clipboard;
            if (clip is not null && !string.IsNullOrEmpty(text))
            {
                await clip.SetTextAsync(text);
            }
        };
        tb.ContextMenu = new ContextMenu { Items = { item } };
        return tb;
    }

    /// <summary>向已挂右键菜单的可选文本块追加一个复制项（如"复制全文/复制表格"，解决跨块多行复制）。</summary>
    public static void AppendMenuItem(SelectableTextBlock tb, string header, Func<string> textProvider)
    {
        if (tb.ContextMenu is not ContextMenu menu)
        {
            return;
        }
        var item = new MenuItem { Header = I18n.T(header) };
        item.Click += async (_, _) =>
        {
            var text = textProvider();
            var clip = TopLevel.GetTopLevel(tb)?.Clipboard;
            if (clip is not null && !string.IsNullOrEmpty(text))
            {
                await clip.SetTextAsync(text);
            }
        };
        menu.Items.Add(item);
    }

    /// <summary>把 Markdown 转为纯文本（复制全文用，保留换行结构、去除标记语法）。</summary>
    public static string ToPlainText(string markdown) => Markdown.ToPlainText(markdown ?? "", Pipeline);

    private static void FillInlines(Markdig.Syntax.Inlines.ContainerInline container, Avalonia.Controls.Documents.InlineCollection target)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case EmphasisInline em:
                    var run = new Run { Text = CollectText(em) };
                    run.FontWeight = em.DelimiterCount >= 2 ? FontWeight.Bold : FontWeight.Normal;
                    if (em.DelimiterCount == 1)
                    {
                        run.FontStyle = FontStyle.Italic;
                    }
                    target.Add(run);
                    break;
                case CodeInline code:
                    AddContentInline(target, code.Content, MonoFont, "#9cdcfe");
                    break;
                case LiteralInline lit:
                    AddLiteralWithPaths(target, lit.Content.ToString());
                    break;
                case LinkInline link when link.IsImage:
                    target.Add(new Avalonia.Controls.Documents.Run { Text = "[图片]" });
                    break;
                case LinkInline link:
                    var linkText = CollectText(link);
                    var linkUrl = link.Url ?? "";
                    if (!string.IsNullOrEmpty(linkUrl))
                    {
                        target.Add(MakeLinkChunk(linkText, linkUrl));
                    }
                    else
                    {
                        foreach (var sub in link)
                        {
                            if (sub is LiteralInline lt)
                            {
                                target.Add(new Run { Text = lt.Content.ToString(), Foreground = new SolidColorBrush(Color.Parse("#6cb6ff")) });
                            }
                        }
                    }
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                default:
                    var t = GetInlineText(inline);
                    if (t.Length > 0)
                    {
                        target.Add(new Run { Text = t });
                    }
                    break;
            }
        }
    }

    private static string CollectText(ContainerInline container)
    {
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            sb.Append(inline switch
            {
                LiteralInline lit => lit.Content.ToString(),
                CodeInline c => c.Content,
                _ => GetInlineText(inline),
            });
        }
        return sb.ToString();
    }

    private static string GetInlineText(Markdig.Syntax.Inlines.Inline? inline)
    {
        if (inline is null) return "";
        if (inline is LiteralInline lit) return lit.Content.ToString();
        if (inline is CodeInline c) return c.Content;
        if (inline is LineBreakInline) return "\n";
        if (inline is ContainerInline container)
        {
            var sb = new StringBuilder();
            foreach (var child in container)
            {
                sb.Append(GetInlineText(child));
            }
            return sb.ToString();
        }
        return inline is ContainerInline cc ? GetInlineText(cc) : "";
    }

    private static Control RenderCodeBlock(CodeBlock code)
    {
        var lines = code.Lines.ToString();

        // 单行内容若是工作区内真实存在的文件路径 → 保留代码块样式，整块可点击打开
        var trimmed = lines.Trim();
        var tb = Selectable(new SelectableTextBlock
        {
            Text = lines,
            FontFamily = MonoFont,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9cdcfe")),
            TextWrapping = TextWrapping.Wrap,
        });
        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1a1a1a")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 2),
            Child = tb,
        };

        if (trimmed.Split('\n').Length == 1 && TryResolveWorkspaceFile(trimmed) is { } resolved)
        {
            border.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            ToolTip.SetTip(border, resolved);
            var pressed = default(Avalonia.Point?);
            border.PointerPressed += (_, e) =>
            {
                pressed = e.GetCurrentPoint(border).Position;
            };
            border.PointerReleased += (_, e) =>
            {
                // 文本选择拖动不触发打开：仅无位移的单击才打开
                if (pressed is { } p)
                {
                    var pos = e.GetCurrentPoint(border).Position;
                    if (Math.Abs(pos.X - p.X) < 4 && Math.Abs(pos.Y - p.Y) < 4)
                    {
                        OpenTarget(resolved);
                    }
                }
                pressed = null;
            };
        }
        return border;
    }

    private static Control RenderList(ListBlock list)
    {
        var tb = Selectable(new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushLight,
            FontSize = 15,
            Margin = new Thickness(14, 2, 0, 2),
        });
        var index = 1;
        var first = true;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (!first) tb.Inlines!.Add(new LineBreak());
            first = false;
            var prefix = list.IsOrdered ? $"{index}. " : "•  ";
            tb.Inlines!.Add(new Run { Text = prefix, Foreground = BrushLight });
            FillListItemInlines(item, tb.Inlines!);
            index++;
        }
        return tb;
    }

    private static void FillListItemInlines(ListItemBlock item, Avalonia.Controls.Documents.InlineCollection target)
    {
        var first = true;
        foreach (var child in item)
        {
            if (!first) target.Add(new LineBreak());
            first = false;
            if (child is ParagraphBlock p && p.Inline is not null)
            {
                FillInlines(p.Inline, target);
            }
            else
            {
                target.Add(new Run { Text = GetBlockText(child) });
            }
        }
    }

    private static Control RenderQuote(QuoteBlock quote)
    {
        var text = GetContainerText(quote);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#4a6785")),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 4, 0, 4),
            Margin = new Thickness(0, 2),
            Child = Selectable(new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#b0b8c4")),
                FontSize = 14.5,
            }),
        };
    }

    private static Control RenderTable(Table table)
    {
        var grid = new Grid { Margin = new Thickness(0, 4) };
        var rowCount = 0;
        var allRows = new List<List<string>>();   // 供"复制表格"导出 TSV
        foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                cells.Add(GetBlockText(cell).Trim());
            }
            allRows.Add(cells);
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var col = 0;
            foreach (var cellText in cells)
            {
                if (grid.ColumnDefinitions.Count <= col)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                }
                var isHeader = rowCount == 0;
                var cellBorder = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#4a4a4a")),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 4),
                    Child = Selectable(new SelectableTextBlock
                    {
                        Text = cellText,
                        FontSize = 13.5,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = isHeader || rowCount == 1 ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = isHeader || rowCount == 1
                            ? new SolidColorBrush(Color.Parse("#cdd9e5"))
                            : BrushLight,
                    }),
                };
                Grid.SetRow(cellBorder, grid.RowDefinitions.Count - 1);
                Grid.SetColumn(cellBorder, col);
                grid.Children.Add(cellBorder);
                col++;
            }
            rowCount++;
        }
        // "复制表格"：每个单元格的右键菜单都追加整表 TSV 导出（可直接粘贴进 Excel）
        var tsv = string.Join("\n", allRows.Select(r => string.Join("\t", r)));
        foreach (var cellTextBlock in grid.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            AppendMenuItem(cellTextBlock, "复制表格", () => tsv);
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = grid,
            Margin = new Thickness(0, 2),
        };
    }

    private static Control RenderPlainText(Block block)
    {
        var text = GetBlockText(block);
        return Selectable(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = BrushLight });
    }

    private static string GetBlockText(Block block)
    {
        if (block is LeafBlock leaf && leaf.Inline is not null)
        {
            return GetInlineText(leaf.Inline);
        }
        if (block is Markdig.Syntax.ContainerBlock container)
        {
            var sb = new StringBuilder();
            foreach (var child in container)
            {
                sb.AppendLine(GetBlockText(child));
            }
            return sb.ToString().TrimEnd();
        }
        return "";
    }

    private static string GetContainerText(Markdig.Syntax.ContainerBlock container)
    {
        var sb = new StringBuilder();
        foreach (var child in container)
        {
            sb.AppendLine(GetBlockText(child));
        }
        return sb.ToString().TrimEnd();
    }

    private static IBrush BrushLight { get; } = new SolidColorBrush(Color.Parse("#e6e6e6"));
    private static IBrush BrushDark { get; } = new SolidColorBrush(Color.Parse("#333333"));

    // ───────────────────────── 可点击目标（链接/文件/图片）─────────────────────────

    /// <summary>行内可点击元素：路径/URL 渲染为链接按钮，点击打开目标。</summary>
    private static void AddContentInline(
        Avalonia.Controls.Documents.InlineCollection target, string text,
        FontFamily font, string color)
    {
        var resolved = TryResolveWorkspaceFile(text);
        if (resolved is not null)
        {
            target.Add(new Avalonia.Controls.Documents.InlineUIContainer
            {
                Child = MakeLinkChunk(text, resolved, mono: true),
            });
            return;
        }
        target.Add(new Run
        {
            Text = text,
            FontFamily = font,
            Foreground = new SolidColorBrush(Color.Parse(color)),
        });
    }

    /// <summary>
    /// 纯文本中的裸路径识别：形如 dir/name.ext 且 workspace 内真实存在的路径
    /// 拆分为「文本 + 可点击链接 + 文本」，使模型输出的报告文件位置可直接打开。
    /// </summary>
    private static void AddLiteralWithPaths(
        Avalonia.Controls.Documents.InlineCollection target, string text)
    {
        // 路径模式：非空白字符组成、含 / 和扩展名
        var pattern = new System.Text.RegularExpressions.Regex(
            @"[\w\-./]+\.[A-Za-z0-9]{2,6}\b");
        var last = 0;
        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(text))
        {
            var candidate = match.Value;
            var resolved = TryResolveWorkspaceFile(candidate);
            if (resolved is null)
            {
                continue;
            }
            if (match.Index > last)
            {
                target.Add(new Run { Text = text[last..match.Index] });
            }
            target.Add(MakeLinkChunk(candidate, resolved, mono: true));
            last = match.Index + candidate.Length;
        }
        if (last == 0)
        {
            if (text.Length > 0)
            {
                target.Add(new Run { Text = text });
            }
            return;
        }
        if (last < text.Length)
        {
            target.Add(new Run { Text = text[last..] });
        }
    }

    /// <summary>构造链接样式的行内按钮（蓝字、悬停下划线、无边框）。</summary>
    private static Button MakeLinkChunk(string text, string target2, bool mono = false)
    {
        var btn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.Parse("#6cb6ff")),
            Padding = new Thickness(2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Tag = target2,
        };
        var run = new Run
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse("#6cb6ff")),
        };
        if (mono)
        {
            run.FontFamily = MonoFont;
        }
        var tb = new TextBlock { Inlines = { run } };
        btn.Content = tb;
        btn.Click += (_, _) => OpenTarget(target2);
        btn.PointerEntered += (_, _) => run.Foreground = new SolidColorBrush(Color.Parse("#9cd2ff"));
        btn.PointerExited += (_, _) => run.Foreground = new SolidColorBrush(Color.Parse("#6cb6ff"));
        return btn;
    }

    /// <summary>路径解析：相对 workspace 或绝对路径，存在则返回绝对路径。</summary>
    private static string? TryResolveWorkspaceFile(string text)
    {
        var t = text.Trim().Trim('"', '\'', '`');
        if (string.IsNullOrEmpty(t) || t.Contains(' ') && !File.Exists(t))
        {
            // 含空格的先按原样尝试
        }
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;   // URL 由 LinkInline 分支处理
        }
        try
        {
            var wsRoot = App.Controller.Agent?.WorkspaceRoot;
            if (!string.IsNullOrEmpty(wsRoot))
            {
                var inWs = Path.GetFullPath(Path.Combine(wsRoot, t));
                var normRoot = wsRoot.EndsWith(Path.DirectorySeparatorChar) ? wsRoot : wsRoot + Path.DirectorySeparatorChar;
                if (inWs.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(inWs))
                {
                    return inWs;
                }
            }
            if (File.Exists(t))
            {
                return t;
            }
        }
        catch { }
        return null;
    }

    /// <summary>打开目标：URL 用系统浏览器，本地文件用系统默认程序（跨平台，跟随系统关联）。</summary>
    public static void OpenTarget(string target)
    {
        try
        {
            // UseShellExecute=true 三端通用：URL 走默认浏览器，本地路径走系统默认程序（Windows ShellExecute / macOS open / Linux xdg-open）。
            // 不能用固定的 "open" 命令：Windows 上不存在，会静默失败导致链接无法打开。
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(target) || Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        }
        catch
        {
            // 打开失败静默（目标可能已被移动/删除）
        }
    }

}
