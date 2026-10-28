using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Windows;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Animation;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace AgentBrook.Helper.Views;

/// <summary>交互业务状态：仅用于头部状态展示，折叠/展开不会改变该状态。</summary>
public enum InteractionState
{
    Pending,    // 等待用户处理（授权/确认/选择/答复）
    Running,    // 执行中（终端/工具调用）
    Done,       // 已完成（含已批准/已答复）
    Rejected,   // 已拒绝
    Cancelled,  // 已取消/已停止
    Failed,     // 执行失败
}

/// <summary>
/// 统一的可折叠交互内容展示容器。
/// 头部：类型图标 + 标题 + 摘要 + 状态 + 折叠箭头（外层折叠，隐藏整个内容卡）。
/// 内容：圆角暗底卡片，长内容限高并提供「展开/收起」入口（内层折叠）。
/// 业务交互逻辑由调用方在内容控件内自行实现，容器不触发任何业务操作。
/// </summary>
public sealed class InteractionCard : StackPanel
{
    private const double CollapsedContentHeight = 160;

    private static readonly StreamGeometry ChevronDown =
        StreamGeometry.Parse("M7.4 8.6 12 13.2l4.6-4.6L18 10l-6 6-6-6z");

    private readonly Border _card;
    private readonly ScrollViewer _scroll;
    private readonly Button _innerToggle;
    private readonly TextBlock _innerToggleText;
    private readonly PathIcon _chevron;
    private readonly TextBlock _statusText;
    private readonly TextBlock _summaryText;
    private readonly Border _shimmer;          // 「执行中」流光带（组合线程动画，零 CPU）
    private CancellationTokenSource? _shimmerCts;
    private bool _innerExpanded;
    private bool _outerCollapsed;

    private readonly bool _collapsible;

    /// <param name="iconData">类型图标路径；为 null 时不显示图标。</param>
    /// <param name="collapsible">内容超限高时提供「展开/收起」入口；false 则完整显示、无滚动条。</param>
    public InteractionCard(string title, Control content, InteractionState state = InteractionState.Pending,
        string? statusText = null, string? iconData = null, bool collapsed = false, bool collapsible = false)
    {
        _collapsible = collapsible;
        Spacing = 4;
        Margin = new Thickness(0, 0, 40, 0);

        // ── 头部：图标 + 标题 + 摘要(*) + 状态 + 折叠箭头；摘要列受宽约束并省略号截断 ──
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        var col = 0;
        if (iconData is not null)
        {
            head.Children.Add(new PathIcon
            {
                Data = StreamGeometry.Parse(iconData),
                Width = 12, Height = 12,
                Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
                VerticalAlignment = VerticalAlignment.Center,
                [Grid.ColumnProperty] = col++,
            });
        }
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")),
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = col++,
        });
        _summaryText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [Grid.ColumnProperty] = col++,
        };
        head.Children.Add(_summaryText);
        _statusText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            [Grid.ColumnProperty] = col++,
        };
        head.Children.Add(_statusText);
        _chevron = new PathIcon
        {
            Data = ChevronDown,
            Width = 12, Height = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            [Grid.ColumnProperty] = col,
        };
        head.Children.Add(_chevron);

        var headBtn = new Button
        {
            Content = head,
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        headBtn.Click += (_, _) => SetOuterCollapsed(!_outerCollapsed);

        // 流光带：210px 宽波动高光（头尾渐隐、波峰居中），平移扫过标题行
        _shimmer = new Border
        {
            Width = 210,
            IsHitTestVisible = false,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = new TranslateTransform(),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(14, 255, 255, 255), 0.22),
                    new GradientStop(Color.FromArgb(44, 255, 255, 255), 0.5),    // 波峰
                    new GradientStop(Color.FromArgb(14, 255, 255, 255), 0.78),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            },
        };
        var headWrap = new Grid { ClipToBounds = true };
        headWrap.Children.Add(_shimmer);      // 先加：绘制在标题文字下层
        headWrap.Children.Add(headBtn);
        Children.Add(headWrap);

        // ── 内容卡：圆角暗底 + 限高滚动 + 内层展开入口 ──
        _innerToggleText = new TextBlock { FontSize = 12 };
        _innerToggle = new Button
        {
            Content = _innerToggleText,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            FontSize = 12,
            Padding = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _innerToggle.Click += (_, _) => SetInnerExpanded(!_innerExpanded);

        _scroll = new ScrollViewer
        {
            MaxHeight = collapsible ? CollapsedContentHeight : double.PositiveInfinity,
            VerticalScrollBarVisibility = collapsible ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Content = content,
        };
        _scroll.ScrollChanged += (_, _) => UpdateInnerToggle();

        var inner = new StackPanel();
        inner.Children.Add(_scroll);
        inner.Children.Add(_innerToggle);

        _card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#242424")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a3a3a")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9),
            Child = inner,
        };
        Children.Add(_card);

        SetState(state, statusText);
        if (collapsed)
        {
            SetOuterCollapsed(true);
        }
    }

    private void SetOuterCollapsed(bool collapsed)
    {
        _outerCollapsed = collapsed;
        _card.IsVisible = !collapsed;
        _chevron.RenderTransform = collapsed ? new RotateTransform(180) : null;
    }

    /// <summary>外层折叠：只留头部一行。</summary>
    public void Collapse() => SetOuterCollapsed(true);

    private void SetInnerExpanded(bool expanded)
    {
        _innerExpanded = expanded;
        _scroll.MaxHeight = expanded ? double.PositiveInfinity : CollapsedContentHeight;
        _scroll.VerticalScrollBarVisibility = expanded ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        UpdateInnerToggleText();
    }

    private void UpdateInnerToggle()
    {
        if (!_collapsible)
        {
            _innerToggle.IsVisible = false;
            return;
        }
        var overflow = !_innerExpanded && _scroll.Extent.Height > CollapsedContentHeight + 4;
        _innerToggle.IsVisible = overflow || _innerExpanded;
        UpdateInnerToggleText();
    }

    private void UpdateInnerToggleText()
    {
        _innerToggleText.Text = _innerExpanded ? $"▴ {I18n.T("ui.collapse")}" : $"▾ {I18n.T("ui.expand")}";
    }

    /// <summary>替换内容卡内部控件（长内容重新计算溢出）。</summary>
    public void SetContent(Control content)
    {
        _innerExpanded = false;
        _innerToggle.IsVisible = false;
        _scroll.MaxHeight = _collapsible ? CollapsedContentHeight : double.PositiveInfinity;
        _scroll.VerticalScrollBarVisibility = _collapsible ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.Content = content;
    }

    /// <summary>更新头部摘要（弱化灰、单行、自动省略；换行压成空格）。</summary>
    public void SetSummary(string? text)
    {
        var t = text ?? "";
        _summaryText.Text = t.Contains('\n') || t.Contains('\r')
            ? System.Text.RegularExpressions.Regex.Replace(t, @"\s*\r?\n\s*", " ")
            : t;
    }

    /// <summary>更新业务状态：统一的状态文案与颜色映射。</summary>
    public void SetState(InteractionState state, string? statusText = null)
    {
        var (key, color) = state switch
        {
            InteractionState.Running => ("ui.state.running", "#2f7bff"),
            InteractionState.Done => ("ui.state.done", "#8a8a8a"),
            InteractionState.Rejected => ("ui.state.rejected", "#ff8a80"),
            InteractionState.Cancelled => ("ui.state.cancelled", "#8a8a8a"),
            InteractionState.Failed => ("ui.state.failed", "#ff8a80"),
            _ => ("ui.state.pending", "#d29922"),
        };
        _statusText.Text = statusText ?? I18n.T(key);
        _statusText.Foreground = new SolidColorBrush(Color.Parse(color));
        SetShimmer(state == InteractionState.Running);   // 执行中：标题行流光扫过
    }

    /// <summary>流光动画开关：TranslateTransform 属性动画（组合线程驱动，不占 CPU）；非执行中立即停止隐藏。</summary>
    private void SetShimmer(bool running)
    {
        _shimmerCts?.Cancel();
        _shimmerCts = null;
        _shimmer.IsVisible = running;
        if (!running)
        {
            return;
        }
        var anim = new Animation
        {
            Duration = TimeSpan.FromSeconds(2.85),   // 慢 50%
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0),    Setters = { new Setter(TranslateTransform.XProperty, -210.0) } },
                new KeyFrame { Cue = new Cue(0.62), Setters = { new Setter(TranslateTransform.XProperty, 900.0) } },
                new KeyFrame { Cue = new Cue(1),    Setters = { new Setter(TranslateTransform.XProperty, 900.0) } },   // 尾段停留作呼吸间隔
            },
        };
        _shimmerCts = new CancellationTokenSource();
        _ = anim.RunAsync(_shimmer, _shimmerCts.Token);
    }
}
