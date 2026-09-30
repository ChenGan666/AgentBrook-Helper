using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
        foreach (var block in doc)
        {
            var c = RenderBlock(block);
            if (c is not null)
            {
                panel.Children.Add(c);
            }
        }
        return panel;
    }

    private static Control? RenderBlock(Block block) => block switch
    {
        HeadingBlock h => new TextBlock
        {
            Text = GetInlineText(h.Inline),
            FontSize = h.Level switch { 1 => 22, 2 => 19, 3 => 17, _ => 16 },
            FontWeight = FontWeight.Bold,
            Foreground = BrushLight,
            Margin = new Thickness(0, h.Level <= 2 ? 8 : 4, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        },
        ParagraphBlock p => RenderParagraph(p),
        Markdig.Syntax.FencedCodeBlock fenced => RenderCodeBlock(fenced),
        Markdig.Syntax.CodeBlock code => RenderCodeBlock(code),
        ListBlock list => RenderList(list),
        QuoteBlock quote => RenderQuote(quote),
        Table table => RenderTable(table),
        ThematicBreakBlock => new Separator { Margin = new Thickness(0, 4), Background = new SolidColorBrush(Color.Parse("#444444")) },
        _ => RenderPlainText(block),
    };

    private static Control RenderParagraph(ParagraphBlock p)
    {
        // 段落仅含一张图片时：块级渲染本地图片
        if (p.Inline is not null)
        {
            var img = p.Inline.FirstOrDefault() as LinkInline;
            if (img is { IsImage: true } && img.FirstChild is LiteralInline li)
            {
                var src = li.Content.ToString();
                var full = src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? src
                    : Path.Combine(App.Controller.Agent!.WorkspaceRoot, src.TrimStart('/'));
                if (File.Exists(full))
                {
                    return new Image
                    {
                        Source = new Bitmap(full),
                        MaxWidth = 380,
                        Margin = new Thickness(0, 4),
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                }
                return new TextBlock
                {
                    Text = $"[图片：{src}（文件不存在）]",
                    Classes = { "procLine" },
                };
            }
        }

        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = BrushLight, FontSize = 15 };
        if (p.Inline is not null)
        {
            FillInlines(p.Inline, tb.Inlines);
        }
        return tb;
    }

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
                    target.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = MonoFont,
                        Foreground = new SolidColorBrush(Color.Parse("#9cdcfe")),
                    });
                    break;
                case LiteralInline lit:
                    target.Add(new Run { Text = lit.Content.ToString() });
                    break;
                case LinkInline link when link.IsImage:
                    target.Add(new Avalonia.Controls.Documents.Run { Text = "[图片]" });
                    break;
                case LinkInline link:
                    foreach (var sub in link)
                    {
                        if (sub is LiteralInline lt)
                        {
                            target.Add(new Run { Text = lt.Content.ToString(), Foreground = new SolidColorBrush(Color.Parse("#6cb6ff")) });
                        }
                    }
                    break;
                case LineBreakInline:
                    target.Add(new Run { Text = "\n" });
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
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1a1a1a")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 2),
            Child = new SelectableTextBlock
            {
                Text = lines,
                FontFamily = MonoFont,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#9cdcfe")),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private static Control RenderList(ListBlock list)
    {
        var panel = new StackPanel { Margin = new Thickness(14, 2, 0, 2), Spacing = 3 };
        var index = 1;
        foreach (var item in list)
        {
            var text = GetBlockText(item);
            var prefix = list.IsOrdered ? $"{index}. " : "•  ";
            var tb = new TextBlock
            {
                Text = prefix + text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushLight,
                FontSize = 15,
            };
            panel.Children.Add(tb);
            index++;
        }
        return panel;
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
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#b0b8c4")),
                FontSize = 14.5,
            },
        };
    }

    private static Control RenderTable(Table table)
    {
        var grid = new Grid { Margin = new Thickness(0, 4) };
        var rowCount = 0;
        foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                cells.Add(GetBlockText(cell).Trim());
            }
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
                    Child = new TextBlock
                    {
                        Text = cellText,
                        FontSize = 13.5,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = isHeader || rowCount == 1 ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = isHeader || rowCount == 1
                            ? new SolidColorBrush(Color.Parse("#cdd9e5"))
                            : BrushLight,
                    },
                };
                Grid.SetRow(cellBorder, grid.RowDefinitions.Count - 1);
                Grid.SetColumn(cellBorder, col);
                grid.Children.Add(cellBorder);
                col++;
            }
            rowCount++;
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
        return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = BrushLight };
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
}
