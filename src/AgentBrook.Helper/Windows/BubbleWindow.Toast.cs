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
    /// <summary>显示一张提示卡；openConversationOnClick=true 时点卡片打开对话窗。</summary>
    public void ShowToast(string text, string accent, TimeSpan? autoClose,
        IEnumerable<(string Label, Action OnClick)>? actions = null, bool openConversationOnClick = false)
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
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
        });
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
        ShowToast(summary, "#ffb340", autoClose: null,
        [
            (I18n.T("ui.approve.2"), () => tcs.TrySetResult(ApprovalDecision.Approve())),
            (I18n.T("ui.full.access"), () =>
            {
                _controller.FullAccess = true;   // 与审批卡"完全访问"同逻辑：本次及后续自动批准，重启复位
                tcs.TrySetResult(ApprovalDecision.Approve());
            }),
            (I18n.T("ui.reject.2"), () => tcs.TrySetResult(ApprovalDecision.Reject())),
        ]);
    }

    /// <summary>A2A 远程工具审批卡：批准 / 完全访问 / 拒绝（与本地审批同逻辑）。</summary>
    public void ShowRemoteApprovalToast(string summary, TaskCompletionSource<ApprovalDecision> tcs)
    {
        ShowToast(summary, "#ffb340", autoClose: null,
        [
            (I18n.T("ui.approve.2"), () => tcs.TrySetResult(ApprovalDecision.Approve())),
            (I18n.T("ui.full.access"), () =>
            {
                _controller.FullAccess = true;
                tcs.TrySetResult(ApprovalDecision.Approve());
            }),
            (I18n.T("ui.reject.2"), () => tcs.TrySetResult(ApprovalDecision.Reject("本机用户拒绝该操作"))),
        ]);
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
        ShowToast($"{peerIcon} {peerDisplayName} 请求配对 · 验证码 {shortCode}", "#ffb340", autoClose: null,
        [
            (I18n.T("ui.message.5"), () => tcs.TrySetResult(true)),
            (I18n.T("ui.reject.2"), () => tcs.TrySetResult(false)),
        ]);
    }

    /// <summary>提问提醒卡：气泡上提示有问题待回答，点「去回答」打开对话窗。</summary>
    public void ShowAskToast(string question, TaskCompletionSource<string> tcs)
    {
        var preview = question.Length > 80 ? question[..80] + "…" : question;
        ShowToast(preview, "#ffb340", autoClose: null,
        [
            (I18n.T("ui.answer.2"), () => _controller.ShowConversation()),
        ]);
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
