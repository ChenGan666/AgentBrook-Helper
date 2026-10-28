using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core;
using AgentBrook.Helper.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using System.Diagnostics;

namespace AgentBrook.Helper.Windows;

public partial class A2ASessionWindow : Window
{
    private readonly A2ASessionViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private ListBox? _eventList;
    private ListBox? _peerSessionList;
    private ListBox? _groupSessionList;
    private Avalonia.Controls.ScrollViewer? _eventScrollViewer;
    private bool _restoringSelection;    // 程序化恢复选中时不触发会话重载
    private bool _programmaticScroll;    // 程序化滚动期间不改变贴底判定
    private A2ASessionItem? _renameTarget;   // 标题编辑的目标群（行内铅笔图标或点击标题进入编辑）
    private Button? _headerTitle;
    private TextBox? _headerEdit;
    private bool _headerEditing;             // 标题编辑中：失焦/回车/ESC 只消费一次
    // 发送框 @ 提记弹层（群会话：@成员 / @全部）
    private Popup? _mentionPopup;
    private ListBox? _mentionList;
    private TextBox? _sendBox;
    private int _mentionStart = -1;
    private bool _suppressMentionUpdate;
    // 伙伴气泡点击：待定位的伙伴 NodeId（会话索引首次加载完成后应用）
    private string? _pendingFocusNodeId;
    private bool _loadedOnce;

    public A2ASessionWindow()
    {
        InitializeComponent();
        _viewModel = new A2ASessionViewModel(App.A2A.Controller);
        DataContext = _viewModel;
        ApplyWindowLanguage();
        I18n.LanguageChanged += ApplyWindowLanguage;
        var peerList = this.FindControl<ListBox>("PeerList")!;
        _peerSessionList = this.FindControl<ListBox>("PeerSessionList")!;
        _groupSessionList = this.FindControl<ListBox>("GroupSessionList")!;
        _eventList = this.FindControl<ListBox>("EventList")!;
        var messageBox = this.FindControl<TextBox>("MessageBox")!;
        // 发送框 @ 提记：文本/光标变化时重算弹层（仅群会话）；隧道阶段拦截导航/选中键
        _mentionPopup = this.FindControl<Popup>("MentionPopup");
        _mentionList = this.FindControl<ListBox>("MentionList");
        _sendBox = messageBox;
        _mentionList?.AddHandler(InputElement.TappedEvent, OnMentionListTapped, RoutingStrategies.Bubble);
        messageBox.PropertyChanged += (_, e) =>
        {
            if (!_suppressMentionUpdate &&
                (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty))
            {
                UpdateMentionPopup();
            }
        };
        messageBox.AddHandler(InputElement.KeyDownEvent, OnMessageBoxKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        // 标题就地编辑：显示态是标题按钮（群主点击群名或行内铅笔进入），编辑态是输入框，失焦/回车保存、ESC 取消
        _headerTitle = this.FindControl<Button>("HeaderTitle");
        _headerEdit = this.FindControl<TextBox>("HeaderEdit");
        _headerEdit!.LostFocus += (_, _) => CommitHeaderEdit(save: true);
        _headerEdit.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    CommitHeaderEdit(save: true);
                    break;
                case Key.Escape:
                    e.Handled = true;
                    CommitHeaderEdit(save: false);
                    break;
            }
        };
        _peerSessionList.SelectionChanged += (_, _) =>
        {
            if (_restoringSelection) return;
            if (_peerSessionList.SelectedItem is A2ASessionItem item)
            {
                _groupSessionList.SelectedItem = null;
                _viewModel.SelectedSession = item;
            }
        };
        _groupSessionList.SelectionChanged += (_, _) =>
        {
            if (_restoringSelection) return;
            if (_groupSessionList.SelectedItem is A2ASessionItem item)
            {
                _peerSessionList.SelectedItem = null;
                _viewModel.SelectedSession = item;
            }
        };
        this.FindControl<Button>("SendBtn")!.Click += async (_, _) =>
        {
            var text = messageBox.Text ?? "";
            await RunUiActionAsync(() => _viewModel.SendAsync(text));
            messageBox.Text = "";
        };
        this.FindControl<ListBox>("InboxList")!.AddHandler(Button.ClickEvent, OnInboxClick, RoutingStrategies.Bubble);
        peerList.AddHandler(Button.ClickEvent, OnPeerRowClick, RoutingStrategies.Bubble);
        _peerSessionList.AddHandler(Button.ClickEvent, OnSessionRowClick, RoutingStrategies.Bubble);
        _groupSessionList.AddHandler(Button.ClickEvent, OnSessionRowClick, RoutingStrategies.Bubble);
        this.FindControl<ListBox>("MemberList")!.AddHandler(Button.ClickEvent, OnMemberRowClick, RoutingStrategies.Bubble);
        _eventList!.AddHandler(Button.ClickEvent, OnEventAttachmentClick, RoutingStrategies.Bubble);

        // 列表每 4 秒重建会因选中项指向旧对象而丢失高亮：VM 的 SelectedSession 变化后，
        // 按 SessionId 把列表选中项恢复到新对象上（程序化恢复不回触发会话重载）。
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(A2ASessionViewModel.SelectedSession))
            {
                Dispatcher.UIThread.Post(RestoreSessionSelection);
            }
        };

        // 智能滚动：记录用户是否贴底；贴底时新事件自动跟随，上翻阅读时不打扰。
        _eventList.TemplateApplied += (_, _) =>
        {
            _eventScrollViewer = _eventList.FindDescendantOfType<Avalonia.Controls.ScrollViewer>();
            if (_eventScrollViewer is not null)
            {
                _eventScrollViewer.ScrollChanged += (_, _) =>
                {
                    if (_programmaticScroll) return;
                    var atBottom = _eventScrollViewer.Extent.Height <= _eventScrollViewer.Viewport.Height ||
                                   _eventScrollViewer.Extent.Height - _eventScrollViewer.Viewport.Height - _eventScrollViewer.Offset.Y < 48;
                    _viewModel.StickyBottom = atBottom;
                };
            }
        };

        // 加载/新事件后的立即滚动：滚动意图在发起时锁定（期间忽略贴底判定，
        // 否则列表重建引发的 ScrollChanged 会把贴底重置、请求被放弃）。
        // 两段推送（Loaded + Background）应对虚拟化容器逐步实测导致的 Extent 增长。
        _viewModel.ScrollToBottomRequested += () =>
        {
            _programmaticScroll = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_eventList.ItemCount == 0)
                {
                    _programmaticScroll = false;
                    return;
                }
                _eventList.ScrollIntoView(_eventList.Items[^1]);
                Dispatcher.UIThread.Post(() =>
                {
                    _eventScrollViewer ??= _eventList.FindDescendantOfType<Avalonia.Controls.ScrollViewer>();
                    if (_eventScrollViewer is not null)
                    {
                        _eventScrollViewer.Offset = new Avalonia.Vector(_eventScrollViewer.Offset.X, _eventScrollViewer.Extent.Height);
                    }
                    _viewModel.ConsumeEventsAppended();
                }, DispatcherPriority.Loaded);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_eventScrollViewer is not null)
                    {
                        _eventScrollViewer.Offset = new Avalonia.Vector(_eventScrollViewer.Offset.X, _eventScrollViewer.Extent.Height);
                    }
                    _programmaticScroll = false;
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        Opened += async (_, _) =>
        {
            ApplyWindowLanguage();
            await _viewModel.RefreshAsync();
            _loadedOnce = true;
            ApplyPendingFocus();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            I18n.LanguageChanged -= ApplyWindowLanguage;
            _viewModel.Dispose();
        };
    }

    /// <summary>按伙伴 NodeId 选中与其已有的 Peer 会话；没有会话时仅打开窗口（不新建会话、不改协议）。</summary>
    public void FocusPeer(string nodeId)
    {
        _pendingFocusNodeId = nodeId;
        if (_loadedOnce)
        {
            ApplyPendingFocus();
        }
    }

    private void ApplyPendingFocus()
    {
        var nodeId = _pendingFocusNodeId;
        _pendingFocusNodeId = null;
        if (nodeId is null || _peerSessionList is null)
        {
            return;
        }
        var item = _viewModel.PeerSessions.FirstOrDefault(x =>
            x.Session.Kind != AgentBrook.A2A.Core.Sessions.SessionKind.Group &&
            x.Session.Members.Any(m => string.Equals(m.NodeId, nodeId, StringComparison.Ordinal)));
        if (item is not null)
        {
            _peerSessionList.SelectedItem = item;
        }
    }

    /// <summary>刷新 A2A 窗口中的静态文案；列表模板中的按钮在虚拟化后也会被覆盖。</summary>
    private void ApplyWindowLanguage()
    {
        Title = I18n.T("a2a.window.title");
        void Text(string name, string key) => this.FindControl<TextBlock>(name)?.SetCurrentValue(TextBlock.TextProperty, I18n.T(key));
        Text("LocalHelperText", "a2a.local.helper");
        Text("PeersHeading", "a2a.peers");
        Text("PeerSessionsHeading", "a2a.peer.sessions");
        Text("GroupSessionsHeading", "a2a.group.sessions");
        Text("MembersHeading", "a2a.members");
        Text("TasksHeading", "a2a.tasks");
        Text("FilesHeading", "a2a.files");
        Text("AuditHeading", "a2a.audit.summary");
        Text("GroupSummaryHeading", "a2a.group.summary.title");
        Text("SummaryDescription", "a2a.summary.description");
        Text("RuntimeHeading", "a2a.runtime.config");
        this.FindControl<TabItem>("DetailsTab")?.SetCurrentValue(TabItem.HeaderProperty, I18n.T("a2a.details"));
        this.FindControl<TabItem>("InboxTab")?.SetCurrentValue(TabItem.HeaderProperty, I18n.T("a2a.inbox"));
        this.FindControl<TabItem>("SummaryTab")?.SetCurrentValue(TabItem.HeaderProperty, I18n.T("a2a.summary"));
        this.FindControl<TabItem>("NodeTab")?.SetCurrentValue(TabItem.HeaderProperty, I18n.T("a2a.node"));
        void Rebind(string name, string path, string format, string fallback)
        {
            var block = this.FindControl<TextBlock>(name);
            block?.Bind(TextBlock.TextProperty, new Binding(path)
            {
                StringFormat = format,
                FallbackValue = fallback,
                TargetNullValue = fallback,
            });
        }
        Rebind("HeaderTitleText", "SelectedSession.Title", "{0}", I18n.T("a2a.event.stream"));
        Rebind("GroupSummaryText", "SelectedGroupSummary", "{0}", I18n.T("a2a.no.summary"));
        Rebind("NodeNameText", "DisplayName", I18n.T("a2a.name.format"), "");
        Rebind("NodeEnabledText", "Enabled", I18n.T("a2a.enabled.format"), "");
        Rebind("NodeConfiguredPortText", "ListenPort", I18n.T("a2a.configured.port.format"), "");
        Rebind("NodeActualPortText", "ActualPort", I18n.T("a2a.actual.port.format"), "");
        Rebind("NodeIdText", "NodeId", I18n.T("a2a.node.id.format"), "");
        var send = this.FindControl<Button>("SendBtn");
        if (send is not null) send.Content = I18n.T("a2a.send");
        var message = this.FindControl<TextBox>("MessageBox");
        if (message is not null) message.Watermark = I18n.T("a2a.message.watermark");
        var autoTalk = this.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Content is string s && (s == "自动交流" || s == "Auto conversation"));
        if (autoTalk is not null)
        {
            autoTalk.Content = I18n.T("a2a.auto.talk");
            ToolTip.SetTip(autoTalk, I18n.T("a2a.auto.talk.tooltip"));
        }
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            switch (button.Tag as string)
            {
                case "peer-pair": button.Content = I18n.T("a2a.pair"); break;
                case "peer-unpair": button.Content = I18n.T("a2a.unpair"); break;
                case "accept": button.Content = I18n.T("a2a.accept"); break;
                case "reject": button.Content = I18n.T("a2a.reject"); break;
                case "member-kick": button.Content = I18n.T("a2a.kick"); ToolTip.SetTip(button, I18n.T("a2a.kick.tooltip")); break;
                case "event-attachment": ToolTip.SetTip(button, I18n.T("a2a.open.attachment")); break;
                case "session-delete": ToolTip.SetTip(button, I18n.T("a2a.delete.session")); break;
                case "group-rename": ToolTip.SetTip(button, I18n.T("a2a.rename.group")); break;
                case "group-close": ToolTip.SetTip(button, I18n.T("a2a.close.group")); break;
            }
        }
    }

    /// <summary>把两个会话列表的选中项恢复到 VM 当前会话（修复周期性重建导致的高亮丢失）。</summary>
    private void RestoreSessionSelection()
    {
        if (_restoringSelection || _peerSessionList is null || _groupSessionList is null) return;
        var target = _viewModel.SelectedSession?.Session.SessionId;
        if (target is null) return;
        _restoringSelection = true;
        try
        {
            var peerMatch = _peerSessionList.Items.OfType<A2ASessionItem>()
                .FirstOrDefault(x => x.Session.SessionId == target);
            var groupMatch = _groupSessionList.Items.OfType<A2ASessionItem>()
                .FirstOrDefault(x => x.Session.SessionId == target);
            if (peerMatch is not null)
            {
                if (!ReferenceEquals(_groupSessionList.SelectedItem, null)) _groupSessionList.SelectedItem = null;
                if (!ReferenceEquals(_peerSessionList.SelectedItem, peerMatch)) _peerSessionList.SelectedItem = peerMatch;
            }
            else if (groupMatch is not null)
            {
                if (!ReferenceEquals(_peerSessionList.SelectedItem, null)) _peerSessionList.SelectedItem = null;
                if (!ReferenceEquals(_groupSessionList.SelectedItem, groupMatch)) _groupSessionList.SelectedItem = groupMatch;
            }
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnInboxClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string action, DataContext: A2AInteraction item }) return;
        _viewModel.ResolveInteraction(item, action == "accept");
        e.Handled = true;
    }

    /// <summary>PEERS 行内按钮：直接对该行 Helper 发起配对 / 解除配对。</summary>
    private void OnPeerRowClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string action, DataContext: A2APeerItem peer }) return;
        switch (action)
        {
            case "peer-pair":
                _ = RunUiActionAsync(() => _viewModel.PairAsync(peer));
                break;
            case "peer-unpair":
                _ = RunUiActionAsync(() => _viewModel.UnpairAsync(peer));
                break;
        }
        e.Handled = true;
    }

    /// <summary>会话列表行内按钮：删除指定会话。</summary>
    private void OnSessionRowClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string action, DataContext: A2ASessionItem item }) return;
        switch (action)
        {
            case "session-delete":
                _ = RunUiActionAsync(() => _viewModel.DeleteSessionAsync(item));
                break;
            case "group-rename":
                StartGroupRename(item);
                break;
            case "group-close":
                // 仅群主可操作（服务层校验，非群主收到明确提示）；成员端经 GroupClosed 广播/补拉跟随置 Closed
                _ = RunUiActionAsync(() => _viewModel.CloseGroupAsync(item));
                break;
        }
        e.Handled = true;
    }

    /// <summary>打开标题就地编辑（预填当前群名）。</summary>
    private void StartGroupRename(A2ASessionItem item)
    {
        if (_groupSessionList.SelectedItem != item)
        {
            // 先选中该群：SelectionChanged 会同步 SelectedSession，标题随之显示目标群
            _groupSessionList.SelectedItem = item;
        }
        EnterHeaderEditMode(item);
    }

    /// <summary>标题进入编辑态：群主点击标题或行内铅笔触发。</summary>
    private void EnterHeaderEditMode(A2ASessionItem item)
    {
        if (_headerEditing) return;
        _headerEditing = true;
        _renameTarget = item;
        _headerEdit!.Text = item.Session.Name ?? "";
        _headerTitle!.IsVisible = false;
        _headerEdit.IsVisible = true;
        _headerEdit.Focus();
        _headerEdit.SelectAll();
    }

    /// <summary>标题退出编辑态：save=true 时群名有变化才走服务层（同步成员），失焦即自动保存。</summary>
    private void CommitHeaderEdit(bool save)
    {
        if (!_headerEditing) return;
        _headerEditing = false;
        var item = _renameTarget;
        _renameTarget = null;
        _headerEdit!.IsVisible = false;
        _headerTitle!.IsVisible = true;
        if (!save || item is null)
        {
            return;
        }
        var newName = _headerEdit.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Session.Name)
        {
            return;
        }
        _ = RunUiActionAsync(() => _viewModel.RenameGroupAsync(item, newName));
    }

    /// <summary>点击标题：仅群主且当前是群组会话时进入编辑；Peer 会话标题（对方名称）不可编辑。</summary>
    private void OnHeaderTitleClick(object? sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedSession;
        if (item?.Session.Kind != AgentBrook.A2A.Core.Sessions.SessionKind.Group || !item.IsCoordinator)
        {
            return;
        }
        EnterHeaderEditMode(item);
    }

    /// <summary>成员行「踢出」：仅群主可见（CanKick 控制），点击走服务层移除并同步所有端。</summary>
    private void OnMemberRowClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: "member-kick", DataContext: A2AMemberItem member }) return;
        if (_viewModel.SelectedSession is not { } item) return;
        _ = RunUiActionAsync(() => _viewModel.RemoveGroupMemberAsync(item, member.NodeId));
        e.Handled = true;
    }

    /// <summary>事件流附件芯片：用系统默认程序打开本机文件（发送方=原文件，接收方=隔离区落盘文件）。</summary>
    private void OnEventAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: "event-attachment", DataContext: A2AEventItem item }) return;
        e.Handled = true;
        var path = item.AttachmentPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _ = ShowNoticeAsync(I18n.T("a2a.attachment.missing"));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = ShowNoticeAsync($"无法打开附件：{ex.Message}");
        }
    }

    // ───────────────────────── 发送框 @ 提记（群成员 / @全部） ─────────────────────────

    /// <summary>文本/光标变化后重算 @ 弹层：仅群会话生效；光标处于活跃 @ 记号中则按过滤词刷新并弹出。</summary>
    private void UpdateMentionPopup()
    {
        if (_mentionPopup is null || _mentionList is null || _sendBox is null
            || _viewModel.SelectedSession?.Session.Kind != AgentBrook.A2A.Core.Sessions.SessionKind.Group)
        {
            CloseMentionPopup();
            return;
        }
        var text = _sendBox.Text ?? "";
        var caret = _sendBox.CaretIndex;
        var start = FindMentionStart(text, caret);
        if (start < 0)
        {
            CloseMentionPopup();
            return;
        }
        var filter = text[(start + 1)..caret];
        var items = new List<MentionItem>();
        if (filter.Length == 0 || "全部".StartsWith(filter, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new MentionItem("@全部", "@全部", "📣", MentionItemKind.All, Online: true, "全群应答"));
        }
        foreach (var (_, name, icon) in _viewModel.GetGroupMemberList(_viewModel.SelectedSession))
        {
            if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new MentionItem($"@{name}", $"@{name}", icon, MentionItemKind.Group, Online: true, "成员"));
            }
        }
        if (items.Count == 0)
        {
            CloseMentionPopup();
            return;
        }
        _mentionStart = start;
        _mentionList.ItemsSource = items;
        _mentionList.SelectedIndex = 0;
        if (!_mentionPopup.IsOpen)
        {
            _mentionPopup.Open();
        }
    }

    /// <summary>从光标向前找活跃 @ 记号：@ 与光标之间无空白（限 25 字符）即有效；找不到返回 -1。</summary>
    private static int FindMentionStart(string text, int caret)
    {
        var end = Math.Min(caret, text.Length);
        for (var i = end - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '@')
            {
                return end - i <= 25 ? i : -1;
            }
            if (char.IsWhiteSpace(ch))
            {
                return -1;
            }
        }
        return -1;
    }

    private void MoveMentionSelection(int delta)
    {
        if (_mentionList is null || _mentionList.ItemCount == 0)
        {
            return;
        }
        var next = Math.Max(0, Math.Min(_mentionList.ItemCount - 1, _mentionList.SelectedIndex + delta));
        _mentionList.SelectedIndex = next;
        _mentionList.ScrollIntoView(next);
    }

    /// <summary>选中项写回发送框：替换活跃 @ 记号段为「@名称 」，光标落在其后，弹层收起。</summary>
    private void AcceptMention()
    {
        if (_sendBox is null || _mentionList?.SelectedItem is not MentionItem item || _mentionStart < 0)
        {
            return;
        }
        var text = _sendBox.Text ?? "";
        var caret = Math.Min(_sendBox.CaretIndex, text.Length);
        var head = text[.._mentionStart] + item.InsertText + " ";
        var tail = text[caret..];
        _suppressMentionUpdate = true;
        try
        {
            _sendBox.Text = head + tail;
            _sendBox.CaretIndex = head.Length;
        }
        finally
        {
            _suppressMentionUpdate = false;
        }
        CloseMentionPopup();
    }

    private void CloseMentionPopup()
    {
        _mentionStart = -1;
        if (_mentionPopup is { IsOpen: true })
        {
            _mentionPopup.Close();
        }
    }

    /// <summary>点击弹层项直接选中并插入。</summary>
    private void OnMentionListTapped(object? sender, TappedEventArgs e)
    {
        if (_mentionList is null || e.Source is not Visual visual)
        {
            return;
        }
        var el = visual;
        while (el is not null && el is not ListBoxItem)
        {
            el = el.GetVisualParent();
        }
        if (el is ListBoxItem { DataContext: MentionItem item })
        {
            if (_mentionList.SelectedItem != item)
            {
                _mentionList.SelectedItem = item;
            }
            AcceptMention();
        }
    }

    /// <summary>弹层打开时的按键路由：↑↓ 选择、Enter/空格/Tab 选中、Esc 关闭；Ctrl/Cmd+Enter 不拦截。</summary>
    private void OnMessageBoxKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_mentionPopup is not { IsOpen: true } || _mentionList is null)
        {
            return;
        }
        var handled = e.Key switch
        {
            Key.Up => MentionMove(-1),
            Key.Down => MentionMove(1),
            Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta) => MentionAccept(),
            Key.Space or Key.Tab => MentionAccept(),
            Key.Escape => MentionClose(),
            _ => false
        };
        if (handled)
        {
            e.Handled = true;
        }
        bool MentionMove(int d)
        {
            MoveMentionSelection(d);
            return true;
        }
        bool MentionAccept()
        {
            AcceptMention();
            return true;
        }
        bool MentionClose()
        {
            CloseMentionPopup();
            return true;
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await ShowNoticeAsync(ex.Message); }
    }

    private async Task ShowNoticeAsync(string message)
    {
        var dialog = new Window
        {
            Title = I18n.T("a2a.notice.title"),
            Width = 420,
            Height = 170,
            CanResize = false,
            Background = Avalonia.Media.Brushes.Black,
            Content = new TextBlock
            {
                Text = message,
                Foreground = Avalonia.Media.Brushes.White,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(20),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };
        await dialog.ShowDialog(this);
    }
}
