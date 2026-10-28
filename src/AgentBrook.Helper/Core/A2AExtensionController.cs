using AgentBrook.Helper.Support;
using AgentBrook.Helper.Windows;
using AgentBrook.Helper.Views;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Transport.Hosting;
using AgentBrook.Agent;
using AgentBrook.Helper.Update;

namespace AgentBrook.Helper.Core;

public sealed class A2AExtensionConfig
{
    public string DisplayName { get; set; } = "";

    /// <summary>对外展示的 Helper 图标（emoji 或图标键），随配对/Profile/会话事件发送给其他 Helper。</summary>
    public string Icon { get; set; } = "🤖";
    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; }

    /// <summary>自动交流默认开启（用户要求）；此为关闭名单——仅在用户明确关闭过某群时记录其 id。</summary>
    public HashSet<string> GroupAutoTalkDisabled { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 收到对端普通消息后由隔离 Agent 自动处理并回传结果（文档 §3.2 执行隔离：
    /// 沙箱目录文件工具、无 shell、3 分钟超时、单并发队列）。关闭时消息仅入会话流，由用户手动处理。
    /// </summary>
    public bool AutoProcessPeerMessages { get; set; } = true;

    /// <summary>群组自动交流默认开启（每群标题栏开关仍可单独关闭）。</summary>
    public bool GroupAutoTalkDefault { get; set; } = true;

    /// <summary>群上下文自动压缩：协调者每 N 轮压缩一次并共享全群。</summary>
    public bool GroupContextCompress { get; set; } = true;

    /// <summary>压缩周期：每 N 轮群消息压缩一次（最小 3）。</summary>
    public int GroupCompressRounds { get; set; } = 10;

    /// <summary>群消息提醒：off=不提醒 mention=仅@我 all=所有消息。</summary>
    public string GroupNotifyMode { get; set; } = "mention";
}

public enum A2AInteractionKind
{
    Pairing,
    TaskInvitation,
    FileReceive,
    RemoteTask
}

public enum A2AInteractionState
{
    Pending,
    Accepted,
    Rejected
}

public sealed class A2AInteraction : INotifyPropertyChanged
{
    private A2AInteractionState _state = A2AInteractionState.Pending;
    private string _detail = "";

    public required string Id { get; init; }
    public required A2AInteractionKind Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get => _detail; set { _detail = value; OnPropertyChanged(nameof(Detail)); } }
    public string? SessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public A2AInteractionState State
    {
        get => _state;
        set
        {
            _state = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(ShowActions));   // 已处理的条目隐藏接受/拒绝按钮
        }
    }

    /// <summary>需要本机用户决策的交互（如配对确认）：Inbox 接受/拒绝时完成，等待方据此继续或放弃。</summary>
    public TaskCompletionSource<bool>? Approval { get; internal set; }

    /// <summary>只有待处理的条目才显示接受/拒绝按钮；已处理的仅保留状态标签。</summary>
    public bool ShowActions => State == A2AInteractionState.Pending;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class A2AExtensionController : IAsyncDisposable
{
    private readonly AssistantController _assistant;
    private readonly A2ARemoteMessageProcessor _remoteProcessor;
    private GroupAutoTalkProcessor? _autoTalkProcessor;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<A2AInteraction> _interactions = new();
    private readonly string _dataDirectory;
    private readonly CancellationTokenSource _stopCts = new();
    private A2AHost? _host;
    private Task? _discoveryTask;

    public A2AExtensionController(AssistantController assistant)
    {
        _assistant = assistant;
        _remoteProcessor = new A2ARemoteMessageProcessor(assistant, () => this);
        _dataDirectory = Path.Combine(UiPrefs.WorkspaceDir(), "a2a");
        Config = LoadConfig();
        CurrentPresence = NewPresence(AgentReadiness.Starting, WorkState.Idle);
        _assistant.ReadyChanged += OnReadyChanged;
        _assistant.StateChanged += OnStateChanged;
    }

    public A2AExtensionConfig Config { get; }
    public A2AHost? Host => _host;
    public bool IsRunning => _host is not null && _host.ActualPort > 0;
    public string? LastError { get; private set; }
    public int ActualPort => _host?.ActualPort ?? 0;
    public string NodeId => _host?.Identity.Identity.NodeId ?? "";
    public PresenceSnapshot CurrentPresence { get; private set; }
    public IReadOnlyList<A2AInteraction> Interactions
    {
        get
        {
            lock (_interactions) return _interactions.ToList();
        }
    }

    public event Action? Changed;

    /// <summary>供协作组件（远程处理器等）在完成阶段刷新 UI。</summary>
    public void NotifyChanged() => Changed?.Invoke();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!Config.Enabled) return;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_host is not null) return;
            Directory.CreateDirectory(_dataDirectory);
            var options = new A2AHostOptions
            {
                DataDirectory = _dataDirectory,
                DisplayName = string.IsNullOrWhiteSpace(Config.DisplayName) ? AssistantIdentity.Current : Config.DisplayName.Trim(),
                Icon = Config.Icon,
                ListenPort = Config.ListenPort,
                BindToLoopback = false
            };
            var host = await Task.Run(() => new A2AHost(options), cancellationToken);
            _host = host;
            host.ProfileProvider = GetProfile;
            // 收到对端普通消息 → 隔离 Agent 自动处理并回传（可在配置中关闭）。
            host.PeerMessageReceived += args =>
            {
                if (!Config.AutoProcessPeerMessages) return;
                _ = _remoteProcessor.HandleAsync(args);
            };
            // 群自动交流：协调者收到成员提交（星型通道）时联动评估；成员端由 PullGroupSyncAsync 触发
            _autoTalkProcessor = new GroupAutoTalkProcessor(_assistant, () => this, TraceAutoTalk);
            host.GroupEventsIngested += (groupId, events) =>
            {
                try
                {
                    var session = host.Store.GetSessionAsync(groupId).GetAwaiter().GetResult();
                    if (session is not { Kind: SessionKind.Group })
                    {
                        return;
                    }
                    UpdateGroupContextFromEvents(groupId, events);                       // 共享摘要入站（成员端跟随）
                    _autoTalkProcessor.OnGroupEventsIngested(session, events);
                    _ = MaybeCompressGroupContextAsync(session);                        // 协调者：满 10 轮压缩并广播
                }
                catch
                {
                    // 联动失败不影响落库
                }
            };

            // §6.4 双端确认：配对必须经本机用户同意。两个入口共享同一结果：
            // 气泡快捷同意/拒绝（不打断当前工作）+ A2A 会话中心 Inbox 卡片留档。
            host.PairingApprovalHook = prompt =>
            {
                var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var direction = prompt.AsResponder ? "请求与你配对" : "发起配对";
                AddInteraction(A2AInteractionKind.Pairing,
                    $"{prompt.PeerIcon} {prompt.PeerDisplayName} {direction}",
                    $"身份 {prompt.PeerNodeId[..Math.Min(20, prompt.PeerNodeId.Length)]}…\n配对验证码：{prompt.ShortCode}（请与对方屏幕上显示的一致）",
                    approval: approval);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var bubble = _assistant.Bubble;
                    if (bubble is not null && bubble.IsVisible)
                    {
                        bubble.ShowPairingToast(prompt.PeerDisplayName, prompt.PeerIcon, prompt.ShortCode, approval);
                    }
                    else
                    {
                        App.ShowA2AWindow();   // 气泡不可用时在 A2A 会话中心确认
                    }
                    if (AssistantIdentity.NotifySoundOn)
                    {
                        NotifySound.Play(AssistantIdentity.NotifySoundId);
                    }
                });
                return approval.Task.WaitAsync(TimeSpan.FromSeconds(120));
            };
            await host.StartAsync(cancellationToken);
            // 持久身份里的名称/图标可能落后于当前配置（如改名后）：以本机配置为准同步，配对与 Card 对外一致。
            var effectiveName = string.IsNullOrWhiteSpace(Config.DisplayName) ? AssistantIdentity.Current : Config.DisplayName.Trim();
            var identity = host.Identity.Identity;
            if (!string.Equals(identity.DisplayName, effectiveName, StringComparison.Ordinal) ||
                !string.Equals(identity.Icon, Config.Icon, StringComparison.Ordinal))
            {
                await host.Identity.UpdateProfileAsync(effectiveName, Config.Icon, cancellationToken);
            }
            ApplyAgentCard();
            LastError = null;
            CurrentPresence = NewPresence(_assistant.Ready ? AgentReadiness.Ready : AgentReadiness.Starting, _assistant.Busy ? WorkState.Busy : WorkState.Idle);
            ApplyPresence();
            _discoveryTask = WatchDiscoveryAsync(host, _stopCts.Token);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Console.Error.WriteLine($"[a2a] start failed: {ex}");
            if (_host is not null)
            {
                try { await _host.DisposeAsync(); } catch { }
                _host = null;
            }
        }
        finally
        {
            _lifecycleGate.Release();
            Changed?.Invoke();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var host = _host;
            _host = null;
            await _stopCts.CancelAsync();
            if (_discoveryTask is not null)
            {
                try { await _discoveryTask; } catch (OperationCanceledException) { }
                _discoveryTask = null;
            }
            if (host is not null) await host.DisposeAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Console.Error.WriteLine($"[a2a] stop failed: {ex}");
        }
        finally
        {
            _lifecycleGate.Release();
            Changed?.Invoke();
        }
    }

    public bool IsGroupAutoTalkEnabled(string groupId) =>
        Config.GroupAutoTalkDefault && !Config.GroupAutoTalkDisabled.Contains(groupId);

    /// <summary>群自动交流决策轨迹（开关未开/@未命中/闸门拦截/触发回合），供 UI 状态栏实时展示排障。</summary>
    public event Action<string>? AutoTalkTrace;

    internal void TraceAutoTalk(string message) => AutoTalkTrace?.Invoke(message);

    public void SetGroupAutoTalk(string groupId, bool enabled)
    {
        var changed = enabled ? Config.GroupAutoTalkDisabled.Remove(groupId) : Config.GroupAutoTalkDisabled.Add(groupId);
        if (changed)
        {
            SaveConfig();
        }
    }

    public void SaveConfig()
    {
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(Path.Combine(_dataDirectory, "config.json"), JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }

    public A2AInteraction? AddInteraction(A2AInteractionKind kind, string title, string detail, string? sessionId = null, TaskCompletionSource<bool>? approval = null)
    {
        A2AInteraction? added = null;
        lock (_interactions)
        {
            if (_interactions.Any(x => x.State == A2AInteractionState.Pending && x.Kind == kind && x.Title == title && x.SessionId == sessionId)) return null;
            added = new A2AInteraction
            {
                Id = Guid.CreateVersion7().ToString("N"),
                Kind = kind,
                Title = title,
                Detail = detail,
                SessionId = sessionId,
                Approval = approval
            };
            _interactions.Insert(0, added);
            // 协作记录上限：进程常驻 + 远程消息每条都入列，不设上限会无界增长；
            // 超出部分从最旧端丢弃，Pending 审批条目保留（其 TCS 仍有等待方）
            const int MaxInteractions = 200;
            if (_interactions.Count > MaxInteractions)
            {
                var overflow = _interactions.Skip(MaxInteractions).ToList();
                _interactions.RemoveRange(MaxInteractions, _interactions.Count - MaxInteractions);
                _interactions.AddRange(overflow.Where(x => x.State == A2AInteractionState.Pending));
            }
            if (approval is not null)
            {
                // 气泡上的批准/拒绝同样回写条目状态（Inbox 按钮随之隐藏，两入口状态一致）。
                var captured = added;
                _ = approval.Task.ContinueWith(t =>
                {
                    captured.State = !t.IsFaulted && t.Result
                        ? A2AInteractionState.Accepted
                        : A2AInteractionState.Rejected;
                    Changed?.Invoke();
                }, TaskScheduler.Default);
            }
        }
        Changed?.Invoke();
        return added;
    }

    public void ResolveInteraction(string id, bool accepted)
    {
        lock (_interactions)
        {
            var item = _interactions.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            item.State = accepted ? A2AInteractionState.Accepted : A2AInteractionState.Rejected;
            item.Approval?.TrySetResult(accepted);
        }
        Changed?.Invoke();
    }

    private bool _remoteBusy;

    /// <summary>是否正在隔离处理远程 A2A 消息（系统退出请求据此决定放行或阻止）。</summary>
    public bool IsRemoteBusy => _remoteBusy;

    /// <summary>远程消息隔离处理期间置 Busy（文档 §11.5：远程执行计入 Presence 负载）。</summary>
    public void SetRemoteBusy(bool busy)
    {
        if (_remoteBusy == busy) return;
        _remoteBusy = busy;
        var work = CurrentPresence.WorkState;
        CurrentPresence = NewPresence(CurrentPresence.AgentState, _remoteBusy ? WorkState.Busy : work);
        ApplyPresence();
        Changed?.Invoke();
    }

    public string CurrentPeerName(string nodeId)
    {
        var peer = _host?.Store.GetPeerAsync(nodeId).GetAwaiter().GetResult();
        return peer?.DisplayName ?? nodeId[..Math.Min(10, nodeId.Length)];
    }

    public string CurrentPeerIcon(string nodeId)
    {
        var peer = _host?.Store.GetPeerAsync(nodeId).GetAwaiter().GetResult();
        return peer?.Icon ?? "🤖";
    }

    /// <summary>
    /// 热更新 Helper 图标（设置页选择后立即生效）：写入配置 + 更新身份文件，
    /// 对端在下次资料刷新/配对时即可看到新图标，无需重启。
    /// </summary>
    public async Task ApplyIconAsync(string icon)
    {
        Config.Icon = icon;
        SaveConfig();
        if (_host is not null)
        {
            await _host.Identity.UpdateProfileAsync(_host.Identity.Identity.DisplayName, icon);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 热更新 Helper 显示名（设置页保存后立即生效）：写入配置 + 更新身份文件 + 刷新 Agent Card，
    /// 对端在下次资料刷新/配对时即可看到新名称，无需重启。
    /// </summary>
    public async Task ApplyDisplayNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        Config.DisplayName = trimmed;
        SaveConfig();
        if (_host is not null)
        {
            await _host.Identity.UpdateProfileAsync(trimmed, _host.Identity.Identity.Icon);
            ApplyAgentCard();
        }
        Changed?.Invoke();
    }

    public PeerProfile GetProfile()
    {
        var models = _assistant.Ready && !string.IsNullOrWhiteSpace(_assistant.Agent!.CurrentModel)
            ? new List<ModelCapability> { new() { Id = _assistant.Agent.CurrentModel, Modalities = ["text"], Roles = ["assistant"] } }
            : new List<ModelCapability>();
        var skills = _assistant.GetSkills().Select(x => new SkillDescriptor { Id = x.Name, Name = x.Name, Version = "1", Tags = ["helper"] }).ToList();
        var memoryGiB = (int)Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024 / 1024);
        return new PeerProfile
        {
            NodeId = NodeId,
            DisplayName = string.IsNullOrWhiteSpace(Config.DisplayName) ? AssistantIdentity.Current : Config.DisplayName,
            Icon = Config.Icon,
            AgentVersion = "Helper 3.0",
            Platform = Environment.OSVersion.ToString(),
            Models = models,
            Skills = skills,
            Device = new DeviceCapability
            {
                CpuClass = Environment.ProcessorCount >= 12 ? "high" : Environment.ProcessorCount >= 6 ? "medium" : "compact",
                LogicalCores = Environment.ProcessorCount,
                MemoryGiB = memoryGiB
            },
            ProfileVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    public async Task PairAsync(A2APeerItem item, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");

        // 已配对节点无需重复配对：刷新资料即可（重复配对会让对端信任态无谓地经历 PairingPending）。
        if (item.Peer.TrustState == PeerTrustState.Trusted)
        {
            await RefreshPeerProfileAsync(host, item.NodeId, cancellationToken);
            Changed?.Invoke();
            return;
        }

        // 用最新发现结果刷新端点（新端点置顶），避免撞已失效的陈旧端口。
        foreach (var ep in host.Discovery.GetDiscovered())
        {
            if (!string.Equals(ep.NodeId, item.NodeId, StringComparison.Ordinal)) continue;
            var fresh = ep.Endpoint.TrimEnd('/') + "/";
            if (IsLoopbackEndpoint(fresh)) continue;
            item.Peer.Endpoints.RemoveAll(x => x.Equals(fresh, StringComparison.OrdinalIgnoreCase));
            item.Peer.Endpoints.Insert(0, fresh);
        }
        var endpoints = item.Peer.Endpoints.ToList();
        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                $"「{item.Name}」当前没有可用端点（广播的是回环地址或已下线）。对方可能运行旧版本，请稍后重试。");
        }
        // 逐端点尝试：陈旧端点失败后自动换下一个（对端重启换端口后列表里通常仍有一个可用）。
        Exception? lastError = null;
        var paired = false;
        foreach (var endpoint in endpoints)
        {
            try
            {
                await host.Pairing.PairWithAsync(new Uri(endpoint), cancellationToken);
                paired = true;
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = ex; }
        }
        if (!paired)
        {
            throw new InvalidOperationException(
                $"「{item.Name}」的所有端点均无响应（对端可能已休眠/下线）。请确认对方 Helper 正在运行后重试。{(lastError is null ? "" : "最后错误：" + lastError.Message)}");
        }
        await RefreshPeerProfileAsync(host, item.NodeId, cancellationToken);
        Changed?.Invoke();
    }

    // ───────────────────── 群组会话（Group Session）服务层：A2A 会话中心 UI 与主会话 Agent 工具共用 ─────────────────────

    /// <summary>创建群组：群名缺省按成员名自动生成；落库、追加 GroupCreated 并立即推送给各成员——
    /// 成员端靠 AgentBrookExtensionHandler 的事件自愈建出本地群组记录，从而在其会话中心可见。</summary>
    public async Task<A2ASession> CreateGroupAsync(IEnumerable<string> memberNodeIds, string? name = null, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        var identity = host.Identity.Identity;
        var session = A2ASession.CreateGroup(Guid.CreateVersion7().ToString("N"), identity.NodeId);
        foreach (var nodeId in memberNodeIds.Where(x => !string.IsNullOrWhiteSpace(x) && x != identity.NodeId).Distinct(StringComparer.Ordinal))
        {
            // 建群即自动入群（用户要求：无需邀请确认），成员直接 Joined
            session.Members.Add(new SessionMember { NodeId = nodeId, Role = "participant", JoinState = MemberJoinState.Joined });
        }
        if (session.Members.Count <= 1)
        {
            throw new InvalidOperationException("没有有效的群成员：请至少提供一位已配对的 Helper（名称或 nodeId）。");
        }
        session.Name = string.IsNullOrWhiteSpace(name) ? await GenerateGroupNameAsync(host, session.Members.Select(x => x.NodeId), cancellationToken) : name.Trim();
        session.Status = SessionStatus.Active;   // 一期简化：无 MemberJoined 握手，创建即激活
        await host.Store.SaveSessionAsync(session, cancellationToken);

        var created = SessionEvent.Create(session.SessionId, identity.NodeId, SessionEventType.GroupCreated,
            new
            {
                name = session.Name,
                coordinator = identity.NodeId,
                members = session.Members.Select(x => new { nodeId = x.NodeId, role = x.Role, joinState = x.JoinState.ToString() })
            },
            senderName: identity.DisplayName, senderIcon: identity.Icon);
        await host.Store.AppendEventAsync(created, cancellationToken: cancellationToken);
        await PushGroupEventsAsync(host, session, [created], cancellationToken);
        Changed?.Invoke();
        return session;
    }

    /// <summary>重命名群组：本地落库 + GroupRenamed 事件推送，成员端 ingest 跟随改名。</summary>
    public async Task RenameGroupAsync(A2ASession session, string name, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == session.Name)
        {
            return;
        }
        var identity = host.Identity.Identity;
        session.Name = trimmed;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await host.Store.SaveSessionAsync(session, cancellationToken);
        var renamed = SessionEvent.Create(session.SessionId, identity.NodeId, SessionEventType.GroupRenamed,
            new { name = trimmed }, senderName: identity.DisplayName, senderIcon: identity.Icon);
        await host.Store.AppendEventAsync(renamed, cancellationToken: cancellationToken);
        await PushGroupEventsAsync(host, session, [renamed], cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>移除群成员（仅群主/协调者可操作）：从成员表删除并追加 MemberLeft 事件；
    /// 事件推送给剩余成员同步成员表，也单独推给被移者——其端 ingest 识别到自己被移出后将本地群组置为 Closed。</summary>
    public async Task RemoveGroupMemberAsync(A2ASession session, string nodeId, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        var identity = host.Identity.Identity;
        if (!string.Equals(session.CoordinatorNodeId, identity.NodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("只有群主（协调者）可以移除成员。");
        }
        if (nodeId == identity.NodeId)
        {
            throw new InvalidOperationException("不能移除自己。");
        }
        var member = session.Members.FirstOrDefault(x => x.NodeId == nodeId);
        if (member is null)
        {
            return;
        }
        session.Members.Remove(member);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await host.Store.SaveSessionAsync(session, cancellationToken);

        var left = SessionEvent.Create(session.SessionId, identity.NodeId, SessionEventType.MemberLeft,
            new { nodeId, reason = "removed" }, senderName: identity.DisplayName, senderIcon: identity.Icon);
        await host.Store.AppendEventAsync(left, cancellationToken: cancellationToken);
        await PushGroupEventsAsync(host, session, [left], cancellationToken);

        // 被移者已不在 session.Members，单独补推一次；其离线则错过（事件补拉在二期）
        try
        {
            var kicked = await host.Store.GetPeerAsync(nodeId, cancellationToken);
            if (kicked?.Endpoints.FirstOrDefault() is { } kickedEndpoint)
            {
                await host.Client.AppendGroupEventsAsync(new Uri(kickedEndpoint), session.SessionId,
                    [AgentBrook.A2A.Transport.Protocol.SessionEventDto.FromCore(left)], cancellationToken);
            }
        }
        catch
        {
            // 被移者不可达：忽略
        }
        Changed?.Invoke();
    }

    /// <summary>向群组发消息（星型拓扑 §12.3）：协调者本地落库（sequence 即全局序号）并广播；
    /// 成员以 Sequence=0 提交协调者分配全局序号，本端随后靠补拉取回；协调者不可达时降级直推。
    /// auto=true 标记来自群自动交流回合（供各端防循环闸门区分 Agent/人类发言）。</summary>
    public async Task SendGroupMessageAsync(A2ASession session, string text, bool auto = false, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        var identity = host.Identity.Identity;
        var message = SessionEvent.Create(session.SessionId, identity.NodeId, SessionEventType.MessagePosted,
            new { text = text.Trim(), auto }, senderName: identity.DisplayName, senderIcon: identity.Icon);

        if (string.Equals(session.CoordinatorNodeId, identity.NodeId, StringComparison.Ordinal))
        {
            var appended = await host.Store.AppendEventAsync(message, cancellationToken: cancellationToken);
            await TouchGroupSessionAsync(host, session, cancellationToken);
            await PushGroupEventsAsync(host, session, [appended], cancellationToken);
            _ = MaybeCompressGroupContextAsync(session, cancellationToken);   // 协调者发言计入轮次，满 10 条触发压缩
            return;
        }

        if (await SubmitToCoordinatorAsync(host, session, message, cancellationToken))
        {
            return;
        }

        // 协调者不可达：降级为本地落库 + 直推（尽力保持可用；序号由各端补拉时的协调者事实源对齐）
        var local = await host.Store.AppendEventAsync(message, cancellationToken: cancellationToken);
        await TouchGroupSessionAsync(host, session, cancellationToken);
        await PushGroupEventsAsync(host, session, [local], cancellationToken);
    }

    /// <summary>把待分配序号的事件（Sequence=0）提交给协调者。成功返回 true；协调者不可达返回 false。</summary>
    private async Task<bool> SubmitToCoordinatorAsync(A2AHost host, A2ASession session, SessionEvent message, CancellationToken cancellationToken)
    {
        try
        {
            var coordinator = await host.Store.GetPeerAsync(session.CoordinatorNodeId ?? "", cancellationToken);
            if (coordinator?.Endpoints.FirstOrDefault() is not { } endpoint)
            {
                return false;
            }
            message.Sequence = 0;
            await host.Client.AppendGroupEventsAsync(new Uri(endpoint), session.SessionId,
                [AgentBrook.A2A.Transport.Protocol.SessionEventDto.FromCore(message)], cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>成员端增量同步（§12.3 按序补拉）：从协调者拉取新事件 + 群元数据快照（成员表/群名/状态），幂等落库。
    /// 协调者自身是事实源无需拉取。返回是否有更新。</summary>
    public async Task<bool> PullGroupSyncAsync(A2ASession session, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        if (string.Equals(session.CoordinatorNodeId, host.Identity.Identity.NodeId, StringComparison.Ordinal))
        {
            return false;
        }
        var coordinator = await host.Store.GetPeerAsync(session.CoordinatorNodeId ?? "", cancellationToken);
        if (coordinator?.Endpoints.FirstOrDefault() is not { } endpoint)
        {
            return false;
        }

        var changed = false;

        // 1) 事件补拉：afterSequence=本地已有序号，远端幂等去重（event_id 主键）
        var remoteEvents = await host.Client.ReadGroupEventsAsync(new Uri(endpoint), session.SessionId, session.LastSequence, cancellationToken);
        if (remoteEvents.Count > 0)
        {
            var core = new List<SessionEvent>();
            foreach (var dto in remoteEvents)
            {
                var stored = await host.Store.AppendEventAsync(dto.ToCore(), assignLocalSequence: false, cancellationToken);
                core.Add(stored);
            }
            await AgentBrook.A2A.Transport.Protocol.GroupSessionSync.ApplyAsync(host.Store, host.Identity, core, cancellationToken);
            session.LastSequence = Math.Max(session.LastSequence, core.Max(x => x.Sequence));
            await host.Store.SaveSessionAsync(session, cancellationToken);
            changed = true;
            UpdateGroupContextFromEvents(session.SessionId, core);   // 共享摘要入站（补拉路径）
            // 群自动交流评估（成员端）：@本机 的消息经六道闸后触发隔离单轮应答
            _autoTalkProcessor?.OnGroupEventsIngested(session, core);
        }

        // 2) 元数据快照：成员表/群名/状态不随事件走；远端较新时整体采纳
        var remoteSession = await host.Client.ReadGroupSessionAsync(new Uri(endpoint), session.SessionId, cancellationToken);
        if (remoteSession is not null && remoteSession.UpdatedAt > session.UpdatedAt)
        {
            session.Name = remoteSession.Name;
            session.Status = remoteSession.Status;
            session.CoordinatorNodeId = remoteSession.CoordinatorNodeId ?? session.CoordinatorNodeId;
            session.Members = remoteSession.Members.Select(m => new SessionMember
            {
                NodeId = m.NodeId ?? "",
                Role = m.Role ?? "participant",
                JoinState = Enum.TryParse<MemberJoinState>(m.JoinState, out var join) ? join : MemberJoinState.Invited
            }).ToList();
            session.UpdatedAt = remoteSession.UpdatedAt;
            await host.Store.SaveSessionAsync(session, cancellationToken);
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
        return changed;
    }

    /// <summary>关闭群组（仅群主）：本地置 Closed + GroupClosed 事件广播，成员端跟随置 Closed。</summary>
    public async Task CloseGroupAsync(A2ASession session, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        var identity = host.Identity.Identity;
        if (!string.Equals(session.CoordinatorNodeId, identity.NodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("只有群主（协调者）可以关闭群组。");
        }
        if (session.Status == SessionStatus.Closed)
        {
            return;   // 已关闭：避免重复广播 GroupClosed
        }
        session.Status = SessionStatus.Closed;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await host.Store.SaveSessionAsync(session, cancellationToken);
        var closed = SessionEvent.Create(session.SessionId, identity.NodeId, SessionEventType.GroupClosed,
            new { }, senderName: identity.DisplayName, senderIcon: identity.Icon);
        await host.Store.AppendEventAsync(closed, cancellationToken: cancellationToken);
        await PushGroupEventsAsync(host, session, [closed], cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>发言/入群后刷新群会话的 updated_at（成员列表排序依据）。</summary>
    private async Task TouchGroupSessionAsync(A2AHost host, A2ASession session, CancellationToken cancellationToken)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await host.Store.SaveSessionAsync(session, cancellationToken);
    }

    private DateTimeOffset _lastDiscoveryAt = DateTimeOffset.MinValue;

    // 群共享上下文摘要（发起方 Helper 每 N 轮压缩一次并共享全群）：groupId → (摘要, 已压缩至序号)
    private readonly object _groupContextLock = new();
    private readonly Dictionary<string, (string Summary, long Through)> _groupContext = new(StringComparer.Ordinal);

    /// <summary>本机当前持有的群共享摘要（自动交流回合推理用）。</summary>
    public string? GetGroupContextSummary(string groupId)
    {
        lock (_groupContextLock)
        {
            return _groupContext.TryGetValue(groupId, out var v) ? v.Summary : null;
        }
    }

    /// <summary>协调者集中压缩（用户要求：发起方 Helper 每 10 轮压缩一次并共享全群）：
    /// 群内未压缩消息满 10 条时执行一次 LLM 摘要，追加 ContextCompressed 事件并推送成员，
    /// 成员端经该事件同步共享摘要。仅协调者执行；非协调者调用直接返回。</summary>
    public async Task MaybeCompressGroupContextAsync(A2ASession session, CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host is null || !string.Equals(session.CoordinatorNodeId, host.Identity.Identity.NodeId, StringComparison.Ordinal))
        {
            return;
        }
        (string Summary, long Through) state;
        lock (_groupContextLock)
        {
            state = _groupContext.TryGetValue(session.SessionId, out var v) ? v : ("", 0);
        }
        var events = await host.Store.ReadEventsAsync(session.SessionId, cancellationToken: cancellationToken);
        var messages = events
            .Where(e => e.Type is SessionEventType.MessagePosted or SessionEventType.MessageSent)
            .ToList();
        if (!Config.GroupContextCompress)
        {
            return;   // 群上下文自动压缩已关闭
        }
        var pending = messages.Where(m => m.Sequence > state.Through).ToList();
        var rounds = Math.Max(3, Config.GroupCompressRounds);
        if (pending.Count < rounds)
        {
            return;
        }
        var chunk = string.Join("\n", pending.Select(GroupAutoTalkProcessor.FormatEventLine));
        var summary = await GroupAutoTalkProcessor.CompressSummaryAsync(_assistant, state.Summary, chunk);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }
        var through = pending.Max(x => x.Sequence);
        var identity = host.Identity.Identity;
        var evt = SessionEvent.Create(session.SessionId, identity.NodeId, SessionEventType.ContextCompressed,
            new { summary, through }, senderName: identity.DisplayName, senderIcon: identity.Icon);
        await host.Store.AppendEventAsync(evt, cancellationToken: cancellationToken);
        lock (_groupContextLock)
        {
            _groupContext[session.SessionId] = (summary, through);
        }
        Console.Error.WriteLine($"[group-autotalk] 群 {session.SessionId[..Math.Min(8, session.SessionId.Length)]} 上下文已压缩（{pending.Count} 条 → 摘要 {summary.Length} 字）");
        AutoTalkTrace?.Invoke($"群上下文已压缩（近 {pending.Count} 条 → {summary.Length} 字摘要），已同步全群");
        await PushGroupEventsAsync(host, session, [evt], cancellationToken);
    }

    /// <summary>入站事件中的共享摘要（ContextCompressed）→ 更新本机共享群上下文（协调者推送与成员补拉两路共用）。</summary>
    private void UpdateGroupContextFromEvents(string groupId, IReadOnlyList<SessionEvent> events)
    {
        foreach (var e in events)
        {
            if (e.Type != SessionEventType.ContextCompressed)
            {
                continue;
            }
            try
            {
                string? summary = null;
                long through = 0;
                using var doc = string.IsNullOrWhiteSpace(e.PayloadJson) ? null : System.Text.Json.JsonDocument.Parse(e.PayloadJson);
                if (doc?.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("summary", out var sItem) && sItem.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        summary = sItem.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("through", out var tItem) && tItem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        through = tItem.GetInt64();
                    }
                }
                if (string.IsNullOrWhiteSpace(summary))
                {
                    continue;
                }
                lock (_groupContextLock)
                {
                    _groupContext.TryGetValue(groupId, out var cur);
                    if (through >= cur.Through)
                    {
                        _groupContext[groupId] = (summary, Math.Max(through, cur.Through));
                    }
                }
            }
            catch
            {
                // 摘要同步失败不影响其他联动
            }
        }
    }

    /// <summary>群组发现（离线补齐，§12.4）：向所有已配对 Helper 查询"我是成员的活跃群组"，
    /// 本端没有记录的（建群时离线、错过 GroupCreated 推送）按远端元数据落地，事件随后由 PullGroupSyncAsync 全量补齐。
    /// 内置 10s 节流：由会话中心轮询驱动即可，无需额外定时器。返回是否发现了新群组。</summary>
    public async Task<bool> DiscoverRemoteGroupsAsync(CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        if (DateTimeOffset.Now - _lastDiscoveryAt < TimeSpan.FromSeconds(10))
        {
            return false;
        }
        _lastDiscoveryAt = DateTimeOffset.Now;

        var changed = false;
        foreach (var peer in await host.Store.GetPeersAsync(cancellationToken))
        {
            if (peer.TrustState != PeerTrustState.Trusted || peer.Endpoints.FirstOrDefault() is not { } endpoint)
            {
                continue;
            }
            List<AgentBrook.A2A.Transport.Protocol.SessionDto> remoteGroups;
            try
            {
                remoteGroups = await host.Client.ReadMyGroupSessionsAsync(new Uri(endpoint), cancellationToken);
            }
            catch
            {
                continue;   // 该 peer 不可达：跳过，下轮再查
            }
            foreach (var dto in remoteGroups)
            {
                if (string.IsNullOrEmpty(dto.SessionId) ||
                    await host.Store.GetSessionAsync(dto.SessionId, cancellationToken) is not null)
                {
                    continue;
                }
                var session = new A2ASession
                {
                    SessionId = dto.SessionId,
                    Kind = SessionKind.Group,
                    CreatedBy = dto.CreatedBy ?? "",
                    CoordinatorNodeId = dto.CoordinatorNodeId,
                    Name = dto.Name,
                    Status = dto.Status,
                    CreatedAt = dto.UpdatedAt,
                    UpdatedAt = dto.UpdatedAt,
                    Members = dto.Members.Select(m => new SessionMember
                    {
                        NodeId = m.NodeId ?? "",
                        Role = m.Role ?? "participant",
                        JoinState = Enum.TryParse<MemberJoinState>(m.JoinState, out var join) ? join : MemberJoinState.Invited
                    }).ToList(),
                };
                await host.Store.SaveSessionAsync(session, cancellationToken);
                changed = true;
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
        return changed;
    }

    /// <summary>把群组事件尽力推给各成员：离线/无端点的成员跳过（其上线后的事件补拉在二期实现）。</summary>
    private async Task PushGroupEventsAsync(A2AHost host, A2ASession session, List<SessionEvent> events, CancellationToken cancellationToken)
    {
        foreach (var member in session.Members.Where(x => x.NodeId != host.Identity.Identity.NodeId))
        {
            try
            {
                var peer = await host.Store.GetPeerAsync(member.NodeId, cancellationToken);
                if (peer?.Endpoints.FirstOrDefault() is not { } endpoint)
                {
                    continue;
                }
                await host.Client.AppendGroupEventsAsync(new Uri(endpoint), session.SessionId,
                    events.Select(AgentBrook.A2A.Transport.Protocol.SessionEventDto.FromCore).ToList(), cancellationToken);
            }
            catch
            {
                // 单个成员推送失败不阻断建群/发言
            }
        }
    }

    /// <summary>按成员显示名自动生成群名：三人以内「A、B、C 的群组」，人数多或名字过长则「A、B 等 N 人的群组」。</summary>
    private async Task<string> GenerateGroupNameAsync(A2AHost host, IEnumerable<string> memberNodeIds, CancellationToken cancellationToken)
    {
        var names = new List<string> { Config.DisplayName };
        foreach (var nodeId in memberNodeIds)
        {
            var peer = await host.Store.GetPeerAsync(nodeId, cancellationToken);
            names.Add(peer?.DisplayName ?? (nodeId.Length <= 8 ? nodeId : nodeId[..8]));
        }
        var joined = string.Join("、", names);
        if (joined.Length > 24 && names.Count > 2)
        {
            return $"{names[0]}、{names[1]} 等 {names.Count} 人的群组";
        }
        return joined + " 的群组";
    }

    private async Task WatchDiscoveryAsync(A2AHost host, CancellationToken cancellationToken)
    {
        var lastPrune = DateTimeOffset.MinValue;
        await foreach (var discovered in host.Discovery.WatchAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(discovered.NodeId) || string.IsNullOrWhiteSpace(discovered.Endpoint)) continue;
            var peer = await host.Store.GetPeerAsync(discovered.NodeId, cancellationToken) ?? new PeerNode { NodeId = discovered.NodeId };
            var endpoint = discovered.Endpoint.TrimEnd('/') + "/";
            // 远端节点广播回环地址（[::1]/127.0.0.1）说明其版本过旧或配置异常，记录了也连不上；
            // 跳过并把新端点置于首选，同时清掉同网段的旧端口条目（对端重启换端口后列表不会无限膨胀，
            // 心跳/消息也不会先撞一堆陈旧端点超时）。
            if (!IsLoopbackEndpoint(endpoint))
            {
                peer.Endpoints.RemoveAll(x => x.Equals(endpoint, StringComparison.OrdinalIgnoreCase));
                peer.Endpoints.RemoveAll(x => SameSubnet(x, endpoint));
                peer.Endpoints.Insert(0, endpoint);
            }
            peer.LastSeenAt = DateTimeOffset.UtcNow;
            try
            {
                var card = await host.Client.ResolveCardAsync(new Uri(endpoint), cancellationToken);
                if (!string.IsNullOrWhiteSpace(card.Name) &&
                    (peer.DisplayName is null || peer.DisplayName.StartsWith("abk:", StringComparison.Ordinal) ||
                     !string.Equals(peer.DisplayName, card.Name, StringComparison.Ordinal)))
                {
                    peer.DisplayName = card.Name;
                }
            }
            catch
            {
                peer.DisplayName ??= discovered.NodeId;
            }
            // 已配对的节点顺带拉取 Profile，刷新名称与图标（图标不进 Agent Card，配对后才可见）。
            // 30 秒冷却：信标每 2 秒到达，避免每个周期都发起资料请求。
            if (peer.TrustState == PeerTrustState.Trusted)
            {
                try
                {
                    if (!_profileFetchAt.TryGetValue(peer.NodeId, out var lastFetch) ||
                        DateTimeOffset.UtcNow - lastFetch > TimeSpan.FromSeconds(30))
                    {
                        _profileFetchAt[peer.NodeId] = DateTimeOffset.UtcNow;
                        var profile = await host.Client.GetProfileAsync(new Uri(endpoint), cancellationToken);
                        if (!string.IsNullOrWhiteSpace(profile.DisplayName)) peer.DisplayName = profile.DisplayName;
                        if (!string.IsNullOrWhiteSpace(profile.Icon)) peer.Icon = profile.Icon;
                        peer.ProfileJson = JsonSerializer.Serialize(profile.ToCore());
                        peer.ProfileVersion = profile.ProfileVersion;
                        await host.Store.UpsertPeerAsync(peer, cancellationToken);
                    }
                }
                catch { /* Profile 拉取失败不影响发现 */ }
            }
            await host.Store.UpsertPeerAsync(peer, cancellationToken);
            // 发现列表卫生：Discovered 且超过 30 分钟未再出现的节点不会自动消失，这里定期清理，
            // 避免测试/临时节点在 PEERS 列表和 a2a_list_peers 里无限堆积（Trusted 节点永远保留）。
            if (DateTimeOffset.UtcNow - lastPrune > TimeSpan.FromMinutes(1))
            {
                lastPrune = DateTimeOffset.UtcNow;
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
                foreach (var stale in await host.Store.GetPeersAsync(cancellationToken))
                {
                    if (stale.TrustState == PeerTrustState.Discovered &&
                        (stale.LastSeenAt is null || stale.LastSeenAt < cutoff))
                    {
                        await host.Store.DeletePeerAsync(stale.NodeId, cancellationToken);
                    }
                }
            }
            Changed?.Invoke();
        }
    }

    private readonly Dictionary<string, DateTimeOffset> _profileFetchAt = new();

    public async Task UnpairAsync(A2APeerItem item, CancellationToken cancellationToken = default)
    {
        var host = _host ?? throw new InvalidOperationException("A2A 尚未启动");
        if (item.Peer.TrustState != PeerTrustState.Trusted && item.Peer.TrustState != PeerTrustState.PairingPending)
        {
            throw new InvalidOperationException($"「{item.Name}」当前状态为 {item.Peer.TrustState}，无需解除配对。");
        }
        var endpoint = item.Peer.Endpoints.FirstOrDefault(e => !IsLoopbackEndpoint(e));
        if (endpoint is not null)
        {
            try
            {
                await host.Client.SendRevokeNoticeAsync(new Uri(endpoint), cancellationToken);
            }
            catch { /* 对端离线时本端照常解除；对端恢复后其签名请求会被 401 拒绝 */ }
        }
        await host.Pairing.RevokePairingAsync(item.NodeId, cancellationToken);
        Changed?.Invoke();
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return true;
        return uri.Host is "127.0.0.1" or "localhost" or "::1" or "";
    }

    /// <summary>同 /24 网段判定：对端换端口时旧端点可安全清除；跨网段的保留（漫游后可能重新可达）。</summary>
    private static bool SameSubnet(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) || !Uri.TryCreate(b, UriKind.Absolute, out var ub)) return false;
        if (!System.Net.IPAddress.TryParse(ua.Host, out var ipa) || !System.Net.IPAddress.TryParse(ub.Host, out var ipb)) return false;
        var ba = ipa.GetAddressBytes();
        var bb = ipb.GetAddressBytes();
        return ba.Length == 4 && bb.Length == 4 && ba[0] == bb[0] && ba[1] == bb[1] && ba[2] == bb[2];
    }

    private static bool IsUsableEndpoint(string endpoint) => !IsLoopbackEndpoint(endpoint);

    /// <summary>配对（重新）完成后拉取对方可信 Profile，把名称与图标落到本地节点资料。</summary>
    private static async Task RefreshPeerProfileAsync(A2A.Transport.Hosting.A2AHost host, string nodeId, CancellationToken cancellationToken)
    {
        try
        {
            var peer = await host.Store.GetPeerAsync(nodeId, cancellationToken);
            var endpoint = peer?.Endpoints.FirstOrDefault(IsUsableEndpoint);
            if (peer is null || endpoint is null) return;
            var profile = await host.Client.GetProfileAsync(new Uri(endpoint), cancellationToken);
            if (!string.IsNullOrWhiteSpace(profile.DisplayName)) peer.DisplayName = profile.DisplayName;
            if (!string.IsNullOrWhiteSpace(profile.Icon)) peer.Icon = profile.Icon;
            peer.ProfileJson = JsonSerializer.Serialize(profile.ToCore());
            peer.ProfileVersion = profile.ProfileVersion;
            await host.Store.UpsertPeerAsync(peer, cancellationToken);
        }
        catch { /* Profile 拉取失败不影响配对结果 */ }
    }

    private void ApplyPresence()
    {
        _host?.Presence.UpdateLocalState(
            CurrentPresence.Availability,
            CurrentPresence.WorkState,
            CurrentPresence.AgentState,
            CurrentPresence.Mood,
            CurrentPresence.Load.RunningTasks,
            CurrentPresence.Load.QueuedTasks,
            CurrentPresence.ActiveGroupCount);
    }

    private A2AExtensionConfig LoadConfig()
    {
        try
        {
            var file = Path.Combine(_dataDirectory, "config.json");
            if (File.Exists(file)) return JsonSerializer.Deserialize<A2AExtensionConfig>(File.ReadAllText(file)) ?? new A2AExtensionConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[a2a] config failed: {ex.Message}");
        }
        return new A2AExtensionConfig { DisplayName = AssistantIdentity.Current, Enabled = true, ListenPort = 0 };
    }

    private void ApplyAgentCard()
    {
        if (_host is null) return;
        var profile = GetProfile();
        _host.AgentCard.Name = profile.DisplayName ?? AssistantIdentity.Current;
        _host.AgentCard.Description = $"AgentBrook Helper on {profile.Device?.CpuClass ?? "standard"} device";
        _host.AgentCard.Skills = profile.Skills.Select(x => new global::A2A.AgentSkill { Id = x.Id ?? x.Name ?? "skill", Name = x.Name ?? x.Id ?? "Skill", Description = "Helper skill", Tags = x.Tags }).ToList();
    }

    private void OnReadyChanged()
    {
        CurrentPresence = NewPresence(AgentReadiness.Ready, _assistant.Busy ? WorkState.Busy : WorkState.Idle);
        ApplyPresence();
        ApplyAgentCard();
        Changed?.Invoke();
    }

    private void OnStateChanged(BrookRunState state, string? _)
    {
        var work = state switch
        {
            BrookRunState.AwaitingApproval or BrookRunState.AwaitingUserAnswer => WorkState.AwaitingUser,
            BrookRunState.Thinking or BrookRunState.CallingTool => WorkState.Busy,
            _ => WorkState.Idle
        };
        CurrentPresence = NewPresence(_assistant.Ready ? AgentReadiness.Ready : AgentReadiness.Starting, work);
        ApplyPresence();
        Changed?.Invoke();
    }

    private PresenceSnapshot NewPresence(AgentReadiness readiness, WorkState work) => new()
    {
        NodeId = NodeId,
        InstanceId = _host?.Identity.Identity.InstanceId,
        Availability = PresenceAvailability.Online,
        WorkState = _remoteBusy ? WorkState.Busy : work,
        AgentState = readiness,
        ObservedAt = DateTimeOffset.UtcNow,
        Load = new PresenceLoad { RunningTasks = work == WorkState.Busy ? 1 : 0, Capacity = 1 },
        ProfileVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    public async ValueTask DisposeAsync()
    {
        _assistant.ReadyChanged -= OnReadyChanged;
        _assistant.StateChanged -= OnStateChanged;
        await StopAsync();
        _stopCts.Dispose();
        _lifecycleGate.Dispose();
    }
}

public sealed class A2AExtensionHost : IAsyncDisposable
{
    public A2AExtensionHost(AssistantController assistant) => Controller = new A2AExtensionController(assistant);
    public A2AExtensionController Controller { get; }
    public Task StartAsync(CancellationToken cancellationToken = default) => Controller.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => Controller.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => Controller.DisposeAsync();
}
