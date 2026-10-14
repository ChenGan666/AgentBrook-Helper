using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using Avalonia.Media;

namespace AgentBrook.Helper;

public sealed class A2APeerItem : INotifyPropertyChanged
{
    private PeerNode _peer;
    private string _presence;
    private string _mood;

    public A2APeerItem(PeerNode peer, string presence, string mood)
    {
        _peer = peer;
        _presence = presence;
        _mood = mood;
    }

    /// <summary>周期刷新时就地更新数据并通知 UI（不重建集合，消除闪烁）。</summary>
    public void Update(PeerNode peer, string presence, string mood)
    {
        _peer = peer;
        _presence = presence;
        _mood = mood;
        OnPropertyChanged(nameof(Peer));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(NodeIdShort));
        OnPropertyChanged(nameof(Trust));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Presence));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ShowPairButton));
        OnPropertyChanged(nameof(ShowUnpairButton));
    }

    public PeerNode Peer => _peer;
    public string NodeId => _peer.NodeId;
    public string Name => _peer.DisplayName ?? _peer.NodeId[..Math.Min(10, _peer.NodeId.Length)];
    public string Icon => _peer.Icon ?? "🤖";
    public string Trust => _peer.TrustState switch
    {
        PeerTrustState.Discovered => "未配对",
        PeerTrustState.PairingPending => "配对中",
        PeerTrustState.Trusted => "已配对",
        PeerTrustState.Blocked => "已屏蔽",
        PeerTrustState.Revoked => "已解除",
        _ => _peer.TrustState.ToString()
    };
    public string Presence => _presence;
    public string Mood => _mood;

    /// <summary>第一行：对方图标 + 显示名称。</summary>
    public string DisplayName => $"{Icon} {Name}";

    /// <summary>第二行：稳定 nodeId（缩略）。</summary>
    public string NodeIdShort => NodeId.Length <= 24 ? NodeId : NodeId[..24] + "…";

    /// <summary>第三行：在线/信任/心情摘要。</summary>
    public string Summary => $"{Presence}  ·  {Trust}  ·  {Mood}";

    /// <summary>行内「配对」按钮：未建立信任（含已解除）时可发起/重新发起配对。</summary>
    public bool ShowPairButton => _peer.TrustState is PeerTrustState.Discovered or PeerTrustState.Revoked or PeerTrustState.PairingPending;

    /// <summary>行内「解除」按钮：已配对或配对待确认时可单方面解除。</summary>
    public bool ShowUnpairButton => _peer.TrustState is PeerTrustState.Trusted or PeerTrustState.PairingPending;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class A2ASessionItem : INotifyPropertyChanged
{
    private A2ASession _session;

    public A2ASessionItem(A2ASession session, string localNodeId, Func<string, string>? resolveMember)
    {
        _session = session;
        LocalNodeId = localNodeId;
        ResolveMember = resolveMember;
    }

    /// <summary>周期刷新时就地更新会话数据并通知 UI（标题/摘要随之刷新，不重建集合）。</summary>
    public void Update(A2ASession session)
    {
        _session = session;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
    }

    public A2ASession Session => _session;

    /// <summary>本机 nodeId：用于在成员里识别"对方"（会话由任意一方创建，peer 角色可能指向本机）。</summary>
    public string LocalNodeId { get; }

    /// <summary>nodeId → "图标 名称" 解析器（由 ViewModel 按已配对 Peer 资料填充）。</summary>
    public Func<string, string>? ResolveMember { get; }

    /// <summary>本机是否为该群协调者（群主）：控制群组行内「改名 / 关闭」图标按钮的可见性。</summary>
    public bool IsCoordinator => string.Equals(_session.CoordinatorNodeId, LocalNodeId, StringComparison.Ordinal);

    public int Unread { get; set; }
    public string Title
    {
        get
        {
            if (_session.Kind == SessionKind.Group)
            {
                // 群名：创建时按成员名自动生成，可手动重命名；无名旧群回退 groupId 前缀
                return string.IsNullOrWhiteSpace(_session.Name)
                    ? $"群组 {_session.SessionId[..Math.Min(8, _session.SessionId.Length)]}"
                    : _session.Name;
            }
            var other = _session.Members.FirstOrDefault(x => x.NodeId != LocalNodeId)?.NodeId
                ?? _session.Members.FirstOrDefault(x => x.Role == "peer")?.NodeId
                ?? "未知";
            var label = ResolveMember?.Invoke(other) ?? other;
            return label.Length > 24 ? label[..24] + "…" : label;
        }
    }
    public string Summary => Unread > 0 ? $"{Title}  ·  {Unread} 未读" : Title;

    /// <summary>群名等元数据变更后调用：刷新标题/摘要绑定。</summary>
    public void RefreshTitle()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>详情面板「成员」行：图标+名称、Node、角色与在线状态。</summary>
public sealed class A2AMemberItem : INotifyPropertyChanged
{
    public required string NodeId { get; init; }
    public required string NodeIdShort { get; init; }
    public required string DisplayName { get; init; }   // 图标 + 名称；本机成员带「（本机）」后缀
    public required string Role { get; init; }

    /// <summary>本机为群主且该成员不是自己时为 true：成员行悬停显示「踢出」按钮。</summary>
    public required bool CanKick { get; init; }

    /// <summary>在线状态：Presence Online/Away 视为在线，本机恒为在线。</summary>
    public bool Online { get; internal set; }

    public string RoleText => Role switch
    {
        "coordinator" => "协调者",
        "participant" => "成员",
        "owner" => "发起者",
        "peer" => "对方",
        _ => Role
    };
    public string StateText => Online ? "在线" : "离线";
    public IBrush StateBrush => Online ? OnlineBrush : OfflineBrush;

    private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.Parse("#34c759"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#8e8e93"));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>在线状态变化后就地更新绑定（不重建行，避免闪烁）。</summary>
    public void SetOnline(bool online)
    {
        if (Online == online)
        {
            return;
        }
        Online = online;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBrush));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class A2AEventItem
{
    public required SessionEvent Event { get; init; }
    public string Time => Event.Timestamp.LocalDateTime.ToString("HH:mm:ss");
    public string SenderIcon => string.IsNullOrWhiteSpace(Event.SenderIcon) ? "🤖" : Event.SenderIcon!;
    public string SenderName => string.IsNullOrWhiteSpace(Event.SenderName)
        ? Event.SenderNodeId[..Math.Min(10, Event.SenderNodeId.Length)]
        : Event.SenderName!;
    public string Sender => $"{SenderIcon} {SenderName}";
    public bool IsRemote => !string.Equals(Event.SenderName, A2ASessionViewModel.LocalDisplayName, StringComparison.Ordinal);
    public string Type => Event.Type.ToString();
    public string Content
    {
        get
        {
            // 成员进出事件：渲染为友好文案（载荷里只有 nodeId，原始 JSON 对用户无意义）
            if (Event.Type is SessionEventType.MemberJoined or SessionEventType.MemberRejected or SessionEventType.MemberLeft)
            {
                var nodeId = ReadPayloadString("nodeId") ?? "";
                var who = nodeId.Length <= 12 ? nodeId : nodeId[..12] + "…";
                var verb = Event.Type switch
                {
                    SessionEventType.MemberJoined => "加入了群组",
                    SessionEventType.MemberRejected => "拒绝了邀请",
                    _ => "离开了群组"
                };
                return $"{who} {verb}";
            }
            if (string.IsNullOrWhiteSpace(Event.PayloadJson)) return Type;
            try
            {
                using var document = JsonDocument.Parse(Event.PayloadJson);
                foreach (var name in new[] { "text", "message", "title", "file", "name", "summary" })
                    if (document.RootElement.TryGetProperty(name, out var value)) return value.ToString();
            }
            catch { }
            return Event.PayloadJson;
        }
    }

    /// <summary>附件名：文件传输事件（ArtifactOffered/ArtifactReceived）渲染附件芯片。</summary>
    public string? AttachmentName => Event.Type switch
    {
        SessionEventType.ArtifactOffered or SessionEventType.ArtifactReceived => ReadPayloadString("file"),
        _ => null
    };

    /// <summary>附件本机路径：发送方取事件记录的原始路径；接收方经 ArtifactStore 隔离区解析。取不到则不可点击。</summary>
    public string? AttachmentPath => Event.Type switch
    {
        SessionEventType.ArtifactOffered => ReadPayloadString("path"),
        SessionEventType.ArtifactReceived => App.A2A?.Controller?.Host?.ArtifactStore.TryGetLocalPath(ReadPayloadString("artifactId") ?? ""),
        _ => null
    };

    public bool HasAttachment => !string.IsNullOrEmpty(AttachmentName);

    private string? ReadPayloadString(string propertyName)
    {
        try
        {
            using var document = string.IsNullOrWhiteSpace(Event.PayloadJson) ? null : JsonDocument.Parse(Event.PayloadJson);
            if (document?.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}

public sealed class A2ASessionViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>本机 Helper 显示名，用于区分事件流中的本机/远端发送主体。</summary>
    internal static string LocalDisplayName = "";

    private readonly A2AExtensionController _controller;
    private readonly string _sessionsFile;
    private readonly CancellationTokenSource _cts = new();
    private A2ASessionItem? _selectedSession;
    private string _status = "正在初始化 A2A…";
    private string _detail = "";
    private bool _refreshing;

    public A2ASessionViewModel(A2AExtensionController controller)
    {
        _controller = controller;
        LocalDisplayName = controller.Host?.Identity.Identity.DisplayName ?? controller.Config.DisplayName;
        _sessionsFile = Path.Combine(UiPrefs.WorkspaceDir(), "a2a", "helper-sessions.json");
        _controller.Changed += OnControllerChanged;
        // 群自动交流决策轨迹上状态栏（exe 启动时看不到 stderr，这是排障的唯一可见出口）
        _controller.AutoTalkTrace += msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = $"自动交流：{msg}");
    }

    public ObservableCollection<A2APeerItem> Peers { get; } = new();
    public ObservableCollection<A2ASessionItem> PeerSessions { get; } = new();
    public ObservableCollection<A2ASessionItem> GroupSessions { get; } = new();
    public ObservableCollection<A2AEventItem> Events { get; } = new();
    public ObservableCollection<A2AMemberItem> Members { get; } = new();
    public ObservableCollection<string> Tasks { get; } = new();
    public ObservableCollection<string> Artifacts { get; } = new();
    public ObservableCollection<string> Audit { get; } = new();
    public ObservableCollection<A2AInteraction> Inbox { get; } = new();

    public A2ASessionItem? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (ReferenceEquals(_selectedSession, value)) return;
            _selectedSession = value;
            if (value is not null) value.Unread = 0;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAutoTalkVisible));
            OnPropertyChanged(nameof(IsAutoTalkOn));
            OnPropertyChanged(nameof(IsGroupSessionSelected));
            OnPropertyChanged(nameof(SelectedGroupSummary));
            _ = LoadSelectedAsync();
        }
    }

    /// <summary>自动交流开关入口：仅群组会话显示（标题栏开关，每群独立）。</summary>
    public bool IsAutoTalkVisible => SelectedSession?.Session.Kind == SessionKind.Group;

    /// <summary>右侧「摘要」页显示条件：仅群组会话（Peer 会话无摘要）。</summary>
    public bool IsGroupSessionSelected => SelectedSession?.Session.Kind == SessionKind.Group;

    /// <summary>本机 Helper 是否在该群自动应答（默认关；持久化在 a2a/config.json）。</summary>
    public bool IsAutoTalkOn
    {
        get => SelectedSession is not null && _controller.IsGroupAutoTalkEnabled(SelectedSession.Session.SessionId);
        set
        {
            if (SelectedSession is null) return;
            _controller.SetGroupAutoTalk(SelectedSession.Session.SessionId, value);
            OnPropertyChanged();
        }
    }

    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public string Detail { get => _detail; private set { _detail = value; OnPropertyChanged(); } }
    /// <summary>当前选中群组的共享压缩摘要（协调者每 10 轮生成并同步，右侧「摘要」页展示）。</summary>
    public string? SelectedGroupSummary =>
        SelectedSession?.Session.Kind == SessionKind.Group
            ? _controller.GetGroupContextSummary(SelectedSession.Session.SessionId)
                ?? "暂无摘要（群聊每 10 轮自动压缩一次）"
            : null;

    public string DisplayName => _controller.Config.DisplayName;
    public bool Enabled => _controller.Config.Enabled;
    public int ListenPort => _controller.Config.ListenPort;
    public int ActualPort => _controller.ActualPort;
    public string NodeId => _controller.NodeId;
    public PresenceSnapshot Presence => _controller.CurrentPresence;

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var host = _controller.Host;
            if (host is null)
            {
                Status = _controller.Config.Enabled ? $"A2A 不可用：{_controller.LastError ?? "尚未启动"}" : "A2A 已禁用";
                Detail = $"名称 {DisplayName}  ·  配置端口 {ListenPort}";
                return;
            }
            var peers = await host.Store.GetPeersAsync(_cts.Token);
            var remote = host.Presence.GetRemotePresences();
            // 用户补充原则：PEERS 只显示已配对节点 + 在线未配对节点（离线且未配对的无意义条目隐藏）。
            // Presence 心跳只在已配对节点间互发；未配对节点的存活信号是发现信标（65 秒窗口）。
            var beaconAliveCutoff = DateTimeOffset.UtcNow.AddSeconds(-65);
            var desiredPeers = peers
                .Where(p => p.TrustState is PeerTrustState.Trusted or PeerTrustState.PairingPending
                            || (p.LastSeenAt ?? DateTimeOffset.MinValue) > beaconAliveCutoff)
                .OrderBy(x => x.TrustState != PeerTrustState.Trusted)
                .ThenBy(x => x.DisplayName ?? x.NodeId)
                .ToList();
            // 就地更新：避免 Peers.Clear()+重建导致每 4 秒闪烁。
            // 1) 移除已消失的（下线未配对等）。
            for (var i = Peers.Count - 1; i >= 0; i--)
            {
                if (desiredPeers.All(d => d.NodeId != Peers[i].NodeId)) Peers.RemoveAt(i);
            }
            // 2) 追加新增或就地更新已有（保留引用，避免 UI 重绘整行）。
            foreach (var peer in desiredPeers)
            {
                remote.TryGetValue(peer.NodeId, out var presence);
                string presenceText;
                if (presence is not null)
                {
                    presenceText = $"{presence.Availability}/{presence.WorkState}";
                }
                else if (peer.TrustState == PeerTrustState.Discovered)
                {
                    presenceText = (peer.LastSeenAt ?? DateTimeOffset.MinValue) > beaconAliveCutoff ? "在线（未配对）" : "离线（未配对）";
                }
                else
                {
                    presenceText = "离线";
                }
                var mood = presence?.Mood?.Label ?? presence?.Mood?.Code ?? "平静";
                var existing = Peers.FirstOrDefault(x => x.NodeId == peer.NodeId);
                if (existing is not null) existing.Update(peer, presenceText, mood);
                else Peers.Add(new A2APeerItem(peer, presenceText, mood));
                if (peer.TrustState == PeerTrustState.PairingPending)
                    _controller.AddInteraction(A2AInteractionKind.Pairing, $"{peer.DisplayName ?? peer.NodeId} 请求配对", "确认后才可建立可信连接");
            }
            // 群组发现（离线补齐）：向已配对 Helper 查询建群时错过的群组；有新群立即落地并全量补拉消息，
            // 之后由 4s 轮询的 PullGroupSyncAsync 持续增量同步
            if (await _controller.DiscoverRemoteGroupsAsync(_cts.Token))
            {
                await LoadSessionIndexAsync();
                foreach (var item in GroupSessions.ToList())
                {
                    if (item.Session.Kind == SessionKind.Group
                        && !string.Equals(item.Session.CoordinatorNodeId, NodeId, StringComparison.Ordinal))
                    {
                        try
                        {
                            if (await _controller.PullGroupSyncAsync(item.Session, _cts.Token))
                            {
                                item.RefreshTitle();
                            }
                        }
                        catch
                        {
                            // 协调者暂不可达：下次轮询再补
                        }
                    }
                }
                if (SelectedSession is not null)
                {
                    RebuildMembers(SelectedSession);
                }
            }

            await LoadSessionIndexAsync();
            RefreshInbox();
            Status = $"A2A 在线  ·  {_controller.CurrentPresence.WorkState}";
            Detail = $"{DisplayName}  ·  端口 {ActualPort}  ·  Node {NodeId}";
            OnPropertyChanged(nameof(ActualPort));
            OnPropertyChanged(nameof(NodeId));
            // 群会话轮询补拉（§12.3）：成员端从协调者增量取事件 + 元数据快照；有更新时同步标题与成员面板
            if (SelectedSession?.Session.Kind == SessionKind.Group)
            {
                var groupChanged = await _controller.PullGroupSyncAsync(SelectedSession.Session, _cts.Token);
                if (groupChanged)
                {
                    SelectedSession.RefreshTitle();
                }
                UpdateMemberPresence(SelectedSession);
            }
            if (SelectedSession is not null) await LoadSelectedAsync();
            OnPropertyChanged(nameof(SelectedGroupSummary));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"刷新失败：{ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    public async Task PairAsync(A2APeerItem peer)
    {
        Status = $"正在与 {peer.Icon} {peer.Name} 配对…（请在气泡/A2A 窗口确认验证码）";
        try
        {
            await _controller.PairAsync(peer, _cts.Token);
            Status = $"✅ 已与 {peer.Icon} {peer.Name} 完成配对";
        }
        catch
        {
            Status = "A2A 在线";
            throw;
        }
        await RefreshAsync();
    }

    /// <summary>单方面解除配对：通知对端 + 本端置为已解除；重新配对需重新双端确认。</summary>
    public async Task UnpairAsync(A2APeerItem peer)
    {
        Status = $"正在解除与 {peer.Icon} {peer.Name} 的配对…";
        try
        {
            await _controller.UnpairAsync(peer, _cts.Token);
            Status = $"已解除与 {peer.Icon} {peer.Name} 的配对（重新配对需双方再次确认）";
        }
        catch
        {
            Status = "A2A 在线";
            throw;
        }
        await RefreshAsync();
    }

    /// <summary>手动修改群名：服务层落库并推送 GroupRenamed，成员端跟随改名。</summary>
    public async Task RenameGroupAsync(A2ASessionItem item, string name)
    {
        await _controller.RenameGroupAsync(item.Session, name);
        item.RefreshTitle();
    }

    /// <summary>移除群成员（仅群主）：服务层落库并推送 MemberLeft 后，就地刷新成员面板。</summary>
    public async Task RemoveGroupMemberAsync(A2ASessionItem item, string nodeId)
    {
        await _controller.RemoveGroupMemberAsync(item.Session, nodeId);
        RebuildMembers(item);
    }

    /// <summary>关闭群组（仅群主）：广播 GroupClosed，成员端跟随置 Closed。</summary>
    public async Task CloseGroupAsync(A2ASessionItem item)
    {
        await _controller.CloseGroupAsync(item.Session);
        item.RefreshTitle();
    }

    /// <summary>发送框 @ 弹层数据源：群成员显示名与图标（不含本机——@ 自己无意义）。</summary>
    public List<(string NodeId, string Name, string Icon)> GetGroupMemberList(A2ASessionItem item)
    {
        try
        {
            var host = _controller.Host;
            if (host is null)
            {
                return [];
            }
            var peers = host.Store.GetPeersAsync(_cts.Token).GetAwaiter().GetResult();
            return item.Session.Members
                .Where(m => m.NodeId != _controller.NodeId)
                .Select(m =>
                {
                    var p = peers.FirstOrDefault(x => x.NodeId == m.NodeId);
                    var name = string.IsNullOrWhiteSpace(p?.DisplayName)
                        ? (m.NodeId.Length <= 12 ? m.NodeId : m.NodeId[..12])
                        : p!.DisplayName!;
                    var icon = string.IsNullOrWhiteSpace(p?.Icon) ? "🤖" : p!.Icon!;
                    return (m.NodeId, name, icon);
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>按会话成员表重建「成员」面板：图标+名称（本机带后缀）、Node、角色与加入状态；
    /// 本机是群主时，除自己外的成员行显示「踢出」按钮。</summary>
    private void RebuildMembers(A2ASessionItem item)
    {
        var session = item.Session;
        var isCoordinator = string.Equals(session.CoordinatorNodeId, item.LocalNodeId, StringComparison.Ordinal);
        var presences = _controller.Host?.Presence.GetRemotePresences();
        Members.Clear();
        foreach (var member in session.Members)
        {
            var isSelf = member.NodeId == item.LocalNodeId;
            // 本机不在 Peers 资料库里（不会作为别人的 Peer 出现），直接取本机身份的图标+名称，避免回退成截断 nodeId
            var selfIcon = string.IsNullOrWhiteSpace(_controller.Config.Icon) ? "🤖" : _controller.Config.Icon;
            var label = isSelf
                ? $"{selfIcon} {_controller.Config.DisplayName}（本机）"
                : item.ResolveMember?.Invoke(member.NodeId) ?? member.NodeId;
            var online = isSelf;
            if (!isSelf && presences?.TryGetValue(member.NodeId, out var presence) == true
                && presence?.Availability is PresenceAvailability.Online or PresenceAvailability.Away)
            {
                online = true;
            }
            Members.Add(new A2AMemberItem
            {
                NodeId = member.NodeId,
                NodeIdShort = Short(member.NodeId),
                DisplayName = label,
                Role = member.Role,
                Online = online,
                CanKick = isCoordinator && !isSelf
            });
        }
    }

    /// <summary>轮询时就地刷新成员在线状态（Presence 变化不重建行，避免闪烁）。</summary>
    private void UpdateMemberPresence(A2ASessionItem item)
    {
        var presences = _controller.Host?.Presence.GetRemotePresences();
        if (presences is null)
        {
            return;
        }
        foreach (var row in Members)
        {
            presences.TryGetValue(row.NodeId, out var presence);
            row.SetOnline(presence?.Availability is PresenceAvailability.Online or PresenceAvailability.Away
                || row.NodeId == item.LocalNodeId);
        }
    }

    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || SelectedSession is null) return;
        var session = SelectedSession.Session;
        if (session.Kind == SessionKind.Group)
        {
            // 星型拓扑：发言统一走服务层（协调者分配全局序号 / 成员提交协调者），补拉取回后渲染
            await _controller.SendGroupMessageAsync(session, text, cancellationToken: _cts.Token);
            await _controller.PullGroupSyncAsync(session, _cts.Token);
            await LoadSelectedAsync();
            return;
        }

        var host = _controller.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        var identity = host.Identity.Identity;
        await host.Store.AppendEventAsync(SessionEvent.Create(session.SessionId, identity.NodeId, SessionEventType.MessageSent,
            new { text = text.Trim() }, senderName: identity.DisplayName, senderIcon: identity.Icon), cancellationToken: _cts.Token);
        foreach (var member in session.Members.Where(x => x.NodeId != identity.NodeId && x.JoinState is MemberJoinState.Joined or MemberJoinState.Invited))
        {
            var peer = await host.Store.GetPeerAsync(member.NodeId, _cts.Token);
            if (peer?.Endpoints.FirstOrDefault() is not { } endpoint) continue;
            await host.Client.SendMessageAsync(new Uri(endpoint), text.Trim(), session.SessionId, _cts.Token);
        }
        await LoadSelectedAsync();
    }

    public void ResolveInteraction(A2AInteraction item, bool accepted)
    {
        _controller.ResolveInteraction(item.Id, accepted);
        RefreshInbox();
    }

    /// <summary>删除指定会话：从数据库移除 session 及其事件，从集合移除，清空选中并持久化索引。</summary>
    public async Task DeleteSessionAsync(A2ASessionItem item)
    {
        var host = _controller.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        await host.Store.DeleteSessionAsync(item.Session.SessionId, _cts.Token);
        PeerSessions.Remove(item);
        GroupSessions.Remove(item);
        _knownSessionIds.Remove(item.Session.SessionId);
        if (SelectedSession == item)
        {
            SelectedSession = null;
            _loadedSessionId = null;
        }
        await SaveSessionIndexAsync();
    }

    private string? _loadedSessionId;
    private long _loadedThroughSequence;

    /// <summary>用户滚动位置是否贴底（由窗口根据 ScrollChanged 维护）：贴底时新事件自动滚动跟随，上翻阅读时不打扰。</summary>
    public bool StickyBottom { get; set; } = true;

    /// <summary>本轮刷新是否追加了新事件（窗口据此决定是否滚动到底）。</summary>
    public bool EventsAppended { get; private set; }

    /// <summary>请求窗口把事件流滚动到底部（加载完成/新事件追加后立即触发）。</summary>
    public event Action? ScrollToBottomRequested;

    /// <summary>窗口在事件被消费后调用，避免重复滚动。</summary>
    public void ConsumeEventsAppended() => EventsAppended = false;

    private void RequestScrollToBottom() => ScrollToBottomRequested?.Invoke();

    private async Task LoadSelectedAsync()
    {
        if (SelectedSession is null || _controller.Host is null) return;
        var sessionId = SelectedSession.Session.SessionId;
        try
        {
            var host = _controller.Host!;
            if (_loadedSessionId != sessionId)
            {
                // 会话切换：群会话先按序补拉（事件+元数据）保证首屏即最新，然后全量重建（此时清空与滚动回顶是预期行为）。
                if (SelectedSession.Session.Kind == SessionKind.Group)
                {
                    try
                    {
                        await _controller.PullGroupSyncAsync(SelectedSession.Session, _cts.Token);
                    }
                    catch
                    {
                        // 协调者不可达：先用本地已有内容渲染
                    }
                }
                var events = await host.Store.ReadEventsAsync(sessionId, cancellationToken: _cts.Token);
                Events.Clear();
                Members.Clear();
                Tasks.Clear();
                Artifacts.Clear();
                Audit.Clear();
                RebuildMembers(SelectedSession);
                foreach (var entry in events)
                    AppendEvent(entry);
                _loadedSessionId = sessionId;
                _loadedThroughSequence = events.Count > 0 ? events.Max(x => x.Sequence) : 0;
                EventsAppended = true;
                StickyBottom = true;   // 切换会话后跟随到底部
                RequestScrollToBottom();   // 立即定位到最新消息，不等下一轮刷新
            }
            else
            {
                // 同一会话：增量追加，避免 Clear+重建造成的闪烁与滚动定位丢失。
                var fresh = await host.Store.ReadEventsAsync(sessionId, _loadedThroughSequence, _cts.Token);
                EventsAppended = fresh.Count > 0;
                foreach (var entry in fresh)
                    AppendEvent(entry);
                if (fresh.Count > 0)
                {
                    _loadedThroughSequence = fresh.Max(x => x.Sequence);
                    if (StickyBottom)
                    {
                        RequestScrollToBottom();   // 新事件到达且用户贴底：立即跟随到最新
                    }
                }
            }
            RefreshInbox();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = $"会话读取失败：{ex.Message}"; }
    }

    private void AppendEvent(SessionEvent entry)
    {
        if (entry.Type == SessionEventType.ContextCompressed)
        {
            return;   // 压缩摘要不进对话流/审计（在右侧「摘要」页独立展示）
        }
        Events.Add(new A2AEventItem { Event = entry });
        Audit.Add($"#{entry.Sequence} {entry.Type} · {Short(entry.SenderNodeId)} · {entry.Timestamp.LocalDateTime:HH:mm:ss}");
        if (entry.Type is SessionEventType.TaskOffered or SessionEventType.TaskAccepted or SessionEventType.TaskStatusChanged or SessionEventType.TaskCompleted or SessionEventType.TaskStateChanged)
            Tasks.Add($"{entry.Type}: {new A2AEventItem { Event = entry }.Content}");
        if (entry.Type is SessionEventType.ArtifactOffered or SessionEventType.ArtifactAccepted or SessionEventType.ArtifactReceived or SessionEventType.ArtifactPublished)
            Artifacts.Add($"{entry.Type}: {new A2AEventItem { Event = entry }.Content}");
        if (entry.Type == SessionEventType.TaskOffered)
            _controller.AddInteraction(A2AInteractionKind.TaskInvitation, "收到任务邀请", new A2AEventItem { Event = entry }.Content, entry.SessionId);
        if (entry.Type == SessionEventType.ArtifactOffered)
            _controller.AddInteraction(A2AInteractionKind.FileReceive, "收到文件", new A2AEventItem { Event = entry }.Content, entry.SessionId);
    }

    private readonly HashSet<string> _knownSessionIds = new();

    private async Task LoadSessionIndexAsync()
    {
        var selectedId = SelectedSession?.Session.SessionId;
        try
        {
            var host = _controller.Host!;
            var selfId = host.Identity.Identity.NodeId;
            var peers = await host.Store.GetPeersAsync(_cts.Token);
            string ResolveMember(string nodeId)
            {
                var peer = peers.FirstOrDefault(x => x.NodeId == nodeId);
                if (peer is null) return nodeId.Length > 12 ? nodeId[..12] + "…" : nodeId;
                var icon = string.IsNullOrWhiteSpace(peer.Icon) ? "🤖" : peer.Icon!;
                var name = string.IsNullOrWhiteSpace(peer.DisplayName) ? nodeId[..Math.Min(10, nodeId.Length)] : peer.DisplayName!;
                return $"{icon} {name}";
            }
            var sessions = await host.Store.GetSessionsAsync(_cts.Token);
            var peerDesired = sessions.Where(x => x.Kind == SessionKind.Peer).OrderByDescending(x => x.UpdatedAt).ToList();
            var groupDesired = sessions.Where(x => x.Kind == SessionKind.Group).OrderByDescending(x => x.UpdatedAt).ToList();
            A2ASessionItem? newestIncoming = null;

            // 就地更新：移除消失的、更新已有的（标题/摘要随 Session 刷新）、追加新增的。
            for (var i = PeerSessions.Count - 1; i >= 0; i--)
                if (peerDesired.All(d => d.SessionId != PeerSessions[i].Session.SessionId)) PeerSessions.RemoveAt(i);
            for (var i = GroupSessions.Count - 1; i >= 0; i--)
                if (groupDesired.All(d => d.SessionId != GroupSessions[i].Session.SessionId)) GroupSessions.RemoveAt(i);

            foreach (var session in peerDesired)
            {
                var existing = PeerSessions.FirstOrDefault(x => x.Session.SessionId == session.SessionId);
                if (existing is not null) { existing.Update(session); continue; }
                var item = new A2ASessionItem(session, selfId, ResolveMember);
                PeerSessions.Add(item);
                if (!_knownSessionIds.Contains(session.SessionId))
                {
                    _knownSessionIds.Add(session.SessionId);
                    if (selectedId is null && newestIncoming is null) newestIncoming = item;
                }
            }
            foreach (var session in groupDesired)
            {
                var existing = GroupSessions.FirstOrDefault(x => x.Session.SessionId == session.SessionId);
                if (existing is not null) { existing.Update(session); continue; }
                var item = new A2ASessionItem(session, selfId, ResolveMember);
                GroupSessions.Add(item);
                if (!_knownSessionIds.Contains(session.SessionId))
                {
                    _knownSessionIds.Add(session.SessionId);
                }
            }

            // 新出现的会话自动选中，用户无需手点即可看到 Helper 间通讯过程。
            if (SelectedSession is null && newestIncoming is not null)
            {
                SelectedSession = newestIncoming;
            }
        }
        catch (Exception ex) { Status = $"会话索引读取失败：{ex.Message}"; }
    }

    private async Task SaveSessionIndexAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sessionsFile)!);
        var ids = PeerSessions.Concat(GroupSessions).Select(x => x.Session.SessionId).Distinct().ToList();
        await File.WriteAllTextAsync(_sessionsFile, JsonSerializer.Serialize(ids, new JsonSerializerOptions { WriteIndented = true }), _cts.Token);
    }

    private void RefreshInbox()
    {
        // 就地更新：条目对象与控制器共享引用（State/Detail 变化经 INPC 自动刷新 UI），
        // 仅同步集合的增删与顺序，消除周期刷新造成的闪烁。
        var desired = _controller.Interactions.ToList();
        for (var i = Inbox.Count - 1; i >= 0; i--)
        {
            if (desired.All(x => x.Id != Inbox[i].Id)) Inbox.RemoveAt(i);
        }
        foreach (var item in desired)
        {
            if (Inbox.All(x => x.Id != item.Id)) Inbox.Add(item);
        }
        // 顺序对齐（控制器新条目插最前）：仅在与期望顺序不一致时重建。
        if (!Inbox.Select(x => x.Id).SequenceEqual(desired.Select(x => x.Id)))
        {
            Inbox.Clear();
            foreach (var item in desired) Inbox.Add(item);
        }
    }

    private void OnControllerChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    private static string Short(string value) => string.IsNullOrEmpty(value) ? "-" : value[..Math.Min(10, value.Length)];
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _controller.Changed -= OnControllerChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
