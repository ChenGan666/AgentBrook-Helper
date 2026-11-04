using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using AgentBrook.Agent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentBrook.Helper.Windows;

// 提示卡：气泡旁弹出的问候/审批/提问卡（最多 3 张，点按可跳转或就地答复）。
public partial class BubbleWindow
{
    private Rect _lastToastBounds;

    // ───────────────────────── F1/F2：气泡提示卡（问候语 / 审批 / 提问） ─────────────────────────
    /// <summary>剥离文本中的内联图片（data:image/...;base64,...）：头像走 PeerIconView 渲染，正文不留 base64 刷屏。</summary>
    private static readonly System.Text.RegularExpressions.Regex DataUriRe =
        new(@"data:image/[A-Za-z0-9.+-]+;base64,[A-Za-z0-9+/=\r\n]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>显示一张提示卡；openConversationOnClick=true 时点卡片打开对话窗；icon 非空时作为头像渲染在正文左侧。
    /// 返回关闭委托（移除本卡）——供双通道决策同步（卡片/toast 任一侧决议，另一侧收起）。</summary>
    public Action ShowToast(string text, string accent, TimeSpan? autoClose,
        IEnumerable<(string Label, Action OnClick)>? actions = null, bool openConversationOnClick = false,
        string? icon = null)
    {
        _collapseTimer?.Stop();
        SetDockExpanded(true);   // 贴边半隐时来提示卡：先滑出全部，避免卡片被屏幕边裁掉
        EnsureAutoHeight();      // 窗口高度若被固定，恢复随内容增长，否则新卡片会被窗口底边裁掉
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#ee2a2a2a")),
            BorderBrush = new SolidColorBrush(Color.Parse(accent)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8),
            MaxWidth = 228,
        };
        var stack = new StackPanel { Spacing = 6 };
        // 内容过长时出滚动条（上限高度内可滚动阅读，不再截断）
        var scroll = new ScrollViewer
        {
            MaxHeight = 260,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var body = new TextBlock
        {
            Text = DataUriRe.Replace(text, "〔图片〕"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        scroll.Content = icon is null ? (Control)body : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new PeerIconView { Icon = icon, Size = 18 },
                body,
            },
        };
        stack.Children.Add(scroll);
        if (actions is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var (label, onClick) in actions)
            {
                var btn = new Button
                {
                    Content = label,
                    FontSize = 12,
                    Padding = new Thickness(10, 3),
                    Background = new SolidColorBrush(Color.Parse(accent)),
                    Foreground = Brushes.White,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                btn.Click += (_, _) =>
                {
                    onClick();
                    RemoveToast(card);
                };
                row.Children.Add(btn);
            }
            stack.Children.Add(row);
        }
        card.Child = stack;
        if (openConversationOnClick)
        {
            card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            var tap = false;   // 按钮点击也会冒泡到卡片，置位后忽略
            foreach (var child in stack.Children.OfType<StackPanel>())
            {
                child.PointerPressed += (_, _) => tap = true;
            }
            card.PointerReleased += (_, _) =>
            {
                if (!tap)
                {
                    _controller.ShowConversation();
                }
                RemoveToast(card);
            };
        }
        _toastPanel.Children.Add(card);
        while (_toastPanel.Children.Count > 3)
        {
            _toastPanel.Children.RemoveAt(0);   // 最多 3 张，丢最旧
        }
        if (autoClose is { } delay)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RemoveToast(card);
            };
            timer.Start();
        }
        // 布局完成后再更新窗口模式与命中区域，确保新提示卡被纳入可点击范围
        Dispatcher.UIThread.Post(() =>
        {
            UpdateWindowLayoutMode();
            UpdateClickThroughRegion();
        }, DispatcherPriority.Background);
        return () => RemoveToast(card);
    }

    private void RemoveToast(Border card)
    {
        _toastPanel.Children.Remove(card);
        // 强制重新测量并收缩窗口，避免卡片移除后仍残留透明命中区
        _toastPanel.InvalidateMeasure();
        EnsureAutoHeight();
        this.InvalidateMeasure();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateWindowLayoutMode();
            UpdateClickThroughRegion();
        }, DispatcherPriority.Background);
    }

    /// <summary>审批快捷卡：不拉起对话窗，直接在气泡上批准/拒绝（对话流中的卡片状态会同步）。</summary>
    public void ShowApprovalToast(string toolName, string? argsJson, TaskCompletionSource<ApprovalDecision> tcs)
    {
        var summary = _controller.Conversation.SummarizeApprovalPublic(toolName, argsJson);
        var closeToast = ShowToast(summary, "#ffb340", autoClose: null,
        [
            (I18n.T("ui.approve.2"), () => tcs.TrySetResult(ApprovalDecision.Approve())),
            (I18n.T("ui.full.access"), () =>
            {
                _controller.FullAccess = true;   // 与审批卡"完全访问"同逻辑：本次及后续自动批准，重启复位
                tcs.TrySetResult(ApprovalDecision.Approve());
            }),
            (I18n.T("ui.reject.2"), () => tcs.TrySetResult(ApprovalDecision.Reject())),
        ]);
        // 双通道同步：决议可来自对话流卡片/回合超时取消——完成即收起本 toast（避免死按钮残留）
        tcs.Task.ContinueWith(_ => Dispatcher.UIThread.Post(closeToast));
    }

    /// <summary>A2A 远程工具审批卡：批准 / 完全访问 / 拒绝（与本地审批同逻辑）。</summary>
    public void ShowRemoteApprovalToast(string summary, TaskCompletionSource<ApprovalDecision> tcs)
    {
        var closeToast = ShowToast(summary, "#ffb340", autoClose: null,
        [
            (I18n.T("ui.approve.2"), () => tcs.TrySetResult(ApprovalDecision.Approve())),
            (I18n.T("ui.full.access"), () =>
            {
                _controller.FullAccess = true;
                tcs.TrySetResult(ApprovalDecision.Approve());
            }),
            (I18n.T("ui.reject.2"), () => tcs.TrySetResult(ApprovalDecision.Reject("本机用户拒绝该操作"))),
        ]);
        tcs.Task.ContinueWith(_ => Dispatcher.UIThread.Post(closeToast));
    }

    /// <summary>A2A 远程工具审批卡：气泡上直接批准/拒绝（远程处理不进主会话）。</summary>
    public void ShowApprovalToast(string summary, TaskCompletionSource<bool> tcs)
    {
        ShowToast(summary, "#ffb340", autoClose: null,
        [
            (I18n.T("ui.approve.2"), () => tcs.TrySetResult(true)),
            (I18n.T("ui.reject.2"), () => tcs.TrySetResult(false)),
        ]);
    }

    /// <summary>A2A 配对确认卡：气泡上直接同意/拒绝（展示对方名称、图标与两端一致的验证码）。</summary>
    public void ShowPairingToast(string peerDisplayName, string peerIcon, string shortCode, TaskCompletionSource<bool> tcs)
    {
        ShowToast($"{peerDisplayName} 请求配对 · 验证码 {shortCode}", "#ffb340", autoClose: null,
        [
            (I18n.T("ui.message.5"), () => tcs.TrySetResult(true)),
            (I18n.T("ui.reject.2"), () => tcs.TrySetResult(false)),
        ], icon: peerIcon);
    }

    /// <summary>提问提醒卡：气泡上提示有问题待回答，点「去回答」打开对话窗。</summary>
    public void ShowAskToast(string question, TaskCompletionSource<string> tcs)
    {
        var preview = question.Length > 80 ? question[..80] + "…" : question;
        var closeToast = ShowToast(preview, "#ffb340", autoClose: null,
        [
            (I18n.T("ui.answer.2"), () => _controller.ShowConversation()),
        ]);
        tcs.Task.ContinueWith(_ => Dispatcher.UIThread.Post(closeToast));
    }

    /// <summary>有在线伙伴时把提示卡下移到收拢头像下方，互不遮挡。</summary>
    private void UpdateToastOffset()
    {
        const double defaultTop = 110;
        var top = _peerBubbles?.HasPeers == true
            ? Math.Max(defaultTop, 72 + LanPeerBubbleOptions.CollapsedArcRadius + LanPeerBubbleOptions.CollapsedSize / 2 + 6)
            : defaultTop;
        if (Math.Abs(_toastPanel.Margin.Top - top) > 0.1)
        {
            _toastPanel.Margin = new Thickness(0, top, 0, 0);
        }
    }
}
