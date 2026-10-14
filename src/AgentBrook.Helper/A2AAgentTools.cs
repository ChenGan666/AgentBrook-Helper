using System.ComponentModel;
using System.Text.Json;
using AgentBrook.A2A.Core.Business;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Transport.Business;
using Microsoft.Extensions.AI;

namespace AgentBrook.Helper;

/// <summary>
/// 把 A2A 局域网协作能力以工具形式暴露给 BrookAgent（模型 function calling），
/// 使"给局域网中的其他伙伴发送问候"这类自然语言请求可直接落地。
/// 本类位于 Helper（同时引用 Agent.Core 与 A2A.*），依赖方向仍满足文档约束。
/// </summary>
public sealed class A2AAgentTools
{
    private readonly Func<A2AExtensionController?> _controller;

    public A2AAgentTools(Func<A2AExtensionController?> controller)
    {
        _controller = controller;
    }

    /// <summary>注入 BrookAgent 的能力说明（随 capabilityBlock 进入系统提示词）。</summary>
    public const string Instructions = """

        # A2A 局域网协作（跨设备 Helper 通讯）
        本机 Helper 可通过 A2A 协议与同一局域网内的其他 AgentBrook Helper 互相发现、配对和通讯。
        - a2a_status：查看本机节点状态（名称、图标、nodeId、端口）。
        - a2a_list_peers：列出局域网内已发现/已配对的其他 Helper（含对方名称、图标、nodeId、信任与在线状态）。
        - a2a_pair：与指定 Helper 配对建立可信连接（参数支持 nodeId 或名称关键字）。
        - a2a_send_message：向已配对的 Helper 发送文本消息（对方 A2A 会话中心可见，消息携带本机名称与图标）。
        - a2a_report_daily_sales：部门销售场景下，向部门主管 Helper 上报当日销售数据（自动签名、幂等去重）。
        当用户提出"给局域网中的其他伙伴发问候/发消息""向部门主管汇报销售数据"等跨设备协作请求时，
        直接调用上述 a2a_* 工具完成，不要询问用户使用 IM、邮件或系统局域网命令。
        工具返回 ⚠️ 表示对端暂不可达（离线/旧版本/端点失效）：把原因如实告诉用户即可，
        最多重试 1 次，不要循环重试或改用其他工具变体。

        # @ 提及路由约定（用户由输入框 @ 选择器插入，名称与 Helper 显示名一致）
        用户消息中的「@名称」是指定远程接收方的显式指令：
        - 「@名称 内容」→ 调用 a2a_send_message(peer="名称", text="内容")，把内容发给该 Helper。
        - 「@全部 内容」→ 调用 a2a_send_all(text="内容")，发给所有已配对的 Helper。
        - 一条消息含多个 @名称时，为每个目标分别调用一次 a2a_send_message。
        - 名称原样使用（不含 @ 后的空格），不要改写、翻译或拆分。
        - 不带 @ 提及的消息按普通对话处理，不要猜测或默认发给某个 Helper。

        # A2A 群组会话（Group Session，多 Helper 群聊）
        - a2a_create_group：创建群组。members 为逗号分隔的已配对 Helper 名称（如 "小布,小智"）；name 可选，缺省按成员名自动生成。
        - a2a_group_send：向群组发送文本消息。
        - a2a_group_rename：修改群名。
        - a2a_list_groups：列出本机所有群组（名称、成员数、groupId）。
        用户消息中的「#群名」指向群组，路由约定：
        - 「#群名 内容」→ a2a_group_send(group="群名", text="内容")。
        - 「#群名」后接改名指令 → a2a_group_rename(group="群名", newName="新名")。
        - 用户要"建群/拉群"且指定了成员时 → a2a_create_group(members="成员1,成员2")；未指定成员先询问。
        - 群名原样使用（不含 # 后的空格）。
        - 建群即自动入群（无需邀请确认）；移除成员/关闭群组由群主在 A2A 会话中心完成，工具暂不提供，被问到时如实告知。
        """;

    public IEnumerable<AITool> BuildTools() =>
    [
        AIFunctionFactory.Create(Status),
        AIFunctionFactory.Create(ListPeers),
        AIFunctionFactory.Create(Pair),
        AIFunctionFactory.Create(SendMessage),
        AIFunctionFactory.Create(SendAll),
        AIFunctionFactory.Create(CreateGroup),
        AIFunctionFactory.Create(SendGroup),
        AIFunctionFactory.Create(RenameGroup),
        AIFunctionFactory.Create(ListGroups),
        AIFunctionFactory.Create(SendFile),
        AIFunctionFactory.Create(ReportDailySales)
    ];

    [Description("查看本机 Helper 的 A2A 状态：名称、图标、nodeId、监听端口、在线状态")]
    public async Task<string> Status()
    {
        var c = RequireController();
        await Task.CompletedTask;
        return JsonSerializer.Serialize(new
        {
            enabled = c.IsRunning,
            displayName = c.Config.DisplayName,
            icon = c.Config.Icon,
            nodeId = c.NodeId,
            port = c.ActualPort,
            workState = c.CurrentPresence.WorkState.ToString(),
            error = c.LastError
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [Description("列出局域网内已发现和已配对的其他 Helper：名称、图标、nodeId、信任状态、在线/忙闲、心情")]
    public async Task<string> ListPeers()
    {
        var c = RequireController();
        var host = c.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        var peers = await host.Store.GetPeersAsync();
        var presences = host.Presence.GetRemotePresences();
        var list = peers.OrderBy(x => x.DisplayName ?? x.NodeId).Select(p =>
        {
            presences.TryGetValue(p.NodeId, out var presence);
            return new
            {
                nodeId = p.NodeId,
                displayName = p.DisplayName,
                icon = p.Icon ?? "🤖",
                trust = p.TrustState.ToString(),
                availability = presence?.Availability.ToString() ?? "Unknown",
                workState = presence?.WorkState.ToString(),
                mood = presence?.Mood?.Label ?? presence?.Mood?.Code,
                endpoints = p.Endpoints.FirstOrDefault()
            };
        }).ToList();
        return JsonSerializer.Serialize(new { count = list.Count, peers = list },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [Description("与局域网内指定 Helper 建立可信配对。参数 peer 支持 nodeId（可只给前缀）或显示名称关键字")]
    public async Task<string> Pair(
        [Description("目标 Helper 的 nodeId（可前缀）或名称关键字")] string peer)
    {
        var c = RequireController();
        var item = FindPeer(c, peer, requireTrusted: false)
            ?? returnNotFound(peer);
        if (item.Peer.TrustState == PeerTrustState.Trusted)
        {
            return $"已与 {item.Name} {item.Icon} 处于可信配对状态，无需重复配对。";
        }
        RefreshEndpointsFromDiscovery(c, item.Peer);
        var endpoint = item.Peer.Endpoints.FirstOrDefault();
        if (string.IsNullOrEmpty(endpoint) || IsLoopbackEndpoint(endpoint))
        {
            return $"⚠️ 无法与 {item.Name} {item.Icon} 配对：对方当前广播的端点无效或不可达（{endpoint ?? "无"}）。对方可能运行的是旧版本或已下线，请稍后重试，不要反复尝试。";
        }
        await c.PairAsync(item);
        return $"✅ 已与 {item.Name} {item.Icon} 完成配对（nodeId: {ShortId(item.NodeId)}），现在可以 a2a_send_message 发送消息。";
    }

    [Description("向已配对的 Helper 发送文本消息。对方会在其 A2A 会话中心看到本 Helper 的名称、图标与消息内容")]
    public async Task<string> SendMessage(
        [Description("目标 Helper 的 nodeId（可前缀）或名称关键字")] string peer,
        [Description("要发送的文本内容")] string text)
    {
        var c = RequireController();
        var host = c.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        var item = FindPeer(c, peer, requireTrusted: true) ?? returnNotFound(peer);
        RefreshEndpointsFromDiscovery(c, item.Peer);
        var endpoint = item.Peer.Endpoints.FirstOrDefault();
        if (string.IsNullOrEmpty(endpoint) || IsLoopbackEndpoint(endpoint))
        {
            return $"⚠️ 无法发送给 {item.Name} {item.Icon}：对方当前没有可用端点（{endpoint ?? "无"}）。对方可能已下线或运行旧版本，请告知用户即可，不要反复重试。";
        }
        return await SendToPeerAsync(host, item, text);
    }

    [Description("向所有已配对的 Helper 广播同一段文本（@全部 提及时使用）。逐个发送并汇总每端结果")]
    public async Task<string> SendAll(
        [Description("要广播的文本内容")] string text)
    {
        var c = RequireController();
        var host = c.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        var trusted = (await host.Store.GetPeersAsync())
            .Where(p => p.TrustState == PeerTrustState.Trusted)
            .OrderBy(p => p.DisplayName ?? p.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (trusted.Count == 0)
        {
            return "当前没有已配对的 Helper，无处广播。请先与目标 Helper a2a_pair 完成配对。";
        }

        var results = new List<string>();
        var skipped = 0;
        foreach (var node in trusted)
        {
            RefreshEndpointsFromDiscovery(c, node);
            var endpoint = node.Endpoints.FirstOrDefault();
            if (string.IsNullOrEmpty(endpoint) || IsLoopbackEndpoint(endpoint))
            {
                skipped++;
                continue;
            }
            results.Add(await SendToPeerAsync(host, new A2APeerItem(node, "未知", "平静"), text));
        }
        var ok = results.Count(r => r.StartsWith("✅", StringComparison.Ordinal));
        var summary = $"@全部 广播完成：成功 {ok}/{trusted.Count}";
        if (skipped > 0)
        {
            summary += $"，{skipped} 个离线/无端点的已跳过";
        }
        return results.Count == 0 ? summary + "。" : summary + "。\n" + string.Join("\n", results);
    }

    /// <summary>单端点投递：建立/复用配对会话 → 本地留痕 → 逐端点尝试发送（陈旧端点自动换下一个）。</summary>
    private async Task<string> SendToPeerAsync(A2A.Transport.Hosting.A2AHost host, A2APeerItem item, string text)
    {
        var identity = host.Identity.Identity;
        var sessionId = await GetOrCreatePeerSessionIdAsync(host, identity.NodeId, item.NodeId);
        var localEvent = SessionEvent.Create(sessionId, identity.NodeId, SessionEventType.MessageSent,
            new { text }, senderName: identity.DisplayName, senderIcon: identity.Icon);
        await host.Store.AppendEventAsync(localEvent);

        Exception? lastError = null;
        foreach (var candidate in item.Peer.Endpoints)
        {
            try
            {
                var response = await host.Client.SendMessageAsync(new Uri(candidate), text, sessionId);
                var reply = response.Message?.Parts?.Select(p => p.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
                return $"✅ 已送达 {item.Name} {item.Icon}（会话 {ShortId(sessionId)}）。"
                    + (string.IsNullOrWhiteSpace(reply) ? "" : $"对方回执：{reply}");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        return $"⚠️ 无法送达 {item.Name} {item.Icon}：所有已知端点均无响应（对端可能已下线或正在运行旧版本）。请告知用户即可，不要反复重试。";
    }

    // ───────────────────── 群组会话工具（服务逻辑在 A2AExtensionController） ─────────────────────

    [Description("创建 A2A 群组会话。members 为逗号分隔的已配对 Helper 名称或 nodeId 前缀；群名缺省按成员名自动生成，可后续用 a2a_group_rename 修改")]
    public async Task<string> CreateGroup(
        [Description("逗号分隔的已配对 Helper 名称或 nodeId 前缀，如 \"小布,小智\"")] string members,
        [Description("群组名称，可选；缺省自动生成")] string? name = null)
    {
        var c = RequireController();
        var selfNodeId = c.NodeId;
        var nodeIds = new List<string>();
        var displayNames = new List<string>();
        foreach (var token in members.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = FindPeer(c, token, requireTrusted: true) ?? returnNotFound(token);
            if (item.NodeId == selfNodeId || nodeIds.Contains(item.NodeId))
            {
                continue;
            }
            nodeIds.Add(item.NodeId);
            displayNames.Add(item.Name);
        }
        if (nodeIds.Count == 0)
        {
            return "没有可用的群成员：请提供至少一位已配对 Helper 的名称（逗号分隔）。";
        }
        var session = await c.CreateGroupAsync(nodeIds, name);
        return $"✅ 群组「{session.Name}」已创建（成员：{string.Join("、", displayNames)}，groupId: {ShortId(session.SessionId)}）。"
            + "成员已收到建群事件，可在其 A2A 会话中心看到该群；用 a2a_group_send 发送群消息。";
    }

    [Description("向 A2A 群组发送文本消息。group 支持群名（可部分匹配）或 groupId 前缀")]
    public async Task<string> SendGroup(
        [Description("目标群组的名称或 groupId 前缀")] string group,
        [Description("要发送的文本内容")] string text)
    {
        var c = RequireController();
        var session = await FindGroup(c, group)
            ?? throw new InvalidOperationException($"未找到匹配 \"{group}\" 的群组。可先 a2a_list_groups 查看，或用 a2a_create_group 创建。");
        await c.SendGroupMessageAsync(session, text);
        return $"✅ 已发送到群组「{session.Name ?? ShortId(session.SessionId)}」（{session.Members.Count} 名成员）。";
    }

    [Description("修改 A2A 群组名称，变更会同步到所有成员")]
    public async Task<string> RenameGroup(
        [Description("目标群组的名称或 groupId 前缀")] string group,
        [Description("新的群组名称")] string newName)
    {
        var c = RequireController();
        var session = await FindGroup(c, group)
            ?? throw new InvalidOperationException($"未找到匹配 \"{group}\" 的群组。可先 a2a_list_groups 查看。");
        var old = session.Name ?? ShortId(session.SessionId);
        await c.RenameGroupAsync(session, newName);
        return session.Name is null
            ? $"群组「{old}」改名失败。"
            : $"✅ 群组「{old}」已更名为「{session.Name}」。";
    }

    [Description("列出本机所有 A2A 群组：名称、成员数、groupId、最近更新时间")]
    public async Task<string> ListGroups()
    {
        var c = RequireController();
        var host = c.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        var groups = (await host.Store.GetSessionsAsync())
            .Where(x => x.Kind == SessionKind.Group)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new
            {
                groupId = x.SessionId,
                name = x.Name ?? $"群组 {x.SessionId[..8]}",
                members = x.Members.Count,
                status = x.Status.ToString(),
                updatedAt = x.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            })
            .ToList();
        return JsonSerializer.Serialize(new { count = groups.Count, groups }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>按群名（可部分匹配，忽略 # 前缀）或 groupId 前缀查找本机群组。</summary>
    private static async Task<A2ASession?> FindGroup(A2AExtensionController c, string group)
    {
        var host = c.Host;
        if (host is null || string.IsNullOrWhiteSpace(group))
        {
            return null;
        }
        var token = group.Trim().TrimStart('#');
        var groups = (await host.Store.GetSessionsAsync()).Where(x => x.Kind == SessionKind.Group);
        return groups.FirstOrDefault(x => x.SessionId.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault(x => (x.Name ?? "").Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static List<(string GroupId, string Name, int MemberCount)>? _groupCache;
    private static DateTimeOffset _groupCacheAt = DateTimeOffset.MinValue;

    /// <summary>输入框 # 选择器数据源：本机群组（groupId、名称、成员数）。短缓存避免每次击键扫库；A2A 未启动返回空表。</summary>
    public static List<(string GroupId, string Name, int MemberCount)> GetGroups()
    {
        if (_groupCache is not null && DateTimeOffset.Now - _groupCacheAt < TimeSpan.FromSeconds(2))
        {
            return _groupCache;
        }
        var list = new List<(string, string, int)>();
        try
        {
            var host = App.A2A?.Controller?.Host;
            if (host is not null)
            {
                list = host.Store.GetSessionsAsync().GetAwaiter().GetResult()
                    .Where(x => x.Kind == SessionKind.Group)
                    .OrderByDescending(x => x.UpdatedAt)
                    .Select(x => (x.SessionId, x.Name ?? $"群组 {x.SessionId[..8]}", x.Members.Count))
                    .ToList();
            }
        }
        catch
        {
            // A2A 未启动/节点库不可读：# 列表回退为空
        }
        _groupCache = list;
        _groupCacheAt = DateTimeOffset.Now;
        return list;
    }

    [Description("向已配对的 Helper 发送本机文件（自动 SHA256 完整性校验）。对方收到后存入其隔离区并向其用户展示")]
    public async Task<string> SendFile(
        [Description("目标 Helper 的 nodeId（可前缀）或名称关键字")] string peer,
        [Description("要发送的本机文件路径（绝对路径，或相对于工作区的路径）")] string path)
    {
        var c = RequireController();
        var host = c.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        var item = FindPeer(c, peer, requireTrusted: true) ?? returnNotFound(peer);

        // 路径解析：支持绝对路径与工作区相对路径
        var localPath = Path.IsPathRooted(path) ? path : Path.Combine(UiPrefs.WorkspaceDir(), path);
        if (!File.Exists(localPath))
        {
            var candidate = Path.Combine(UiPrefs.WorkspaceDir(), path);
            if (File.Exists(candidate)) localPath = candidate;
            else return $"⚠️ 找不到文件：{path}。请确认路径后重试。";
        }

        var sessionId = await A2AFileTransferService.GetOrCreatePeerSessionIdAsync(host, host.Identity.Identity.NodeId, item.NodeId);
        try
        {
            var descriptor = await A2AFileTransferService.SendFileAsync(host, item.NodeId, localPath,
                sessionId: sessionId, cancellationToken: default);
            return $"✅ 文件已发送给 {item.Name} {item.Icon}：「{descriptor.Name}」（{descriptor.Size} 字节，SHA256 校验通过）。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"⚠️ 发送失败：{ex.Message}。请告知用户即可，不要反复重试。";
        }
    }

    [Description("部门销售场景：向部门主管 Helper 上报本人当日销售数据（自动签名，重复上报自动去重/按版本更新）")]
    public async Task<string> ReportDailySales(
        [Description("当日销售总额（元）")] decimal salesAmount,
        [Description("当日订单数")] int orderCount,
        [Description("当日退款总额（元）")] decimal refundAmount,
        [Description("当日新增客户数")] int newCustomerCount)
    {
        var c = RequireController();
        var host = c.Host ?? throw new InvalidOperationException("A2A 尚未启动");
        var directoryPath = DepartmentDirectory.PathFor(Path.Combine(UiPrefs.WorkspaceDir(), "a2a"));
        if (!File.Exists(directoryPath))
        {
            return "未找到部门目录（workspace/a2a/department-directory.json），无法确定部门主管。请先由管理员建立部门目录。";
        }
        var directory = await DepartmentDirectory.LoadAsync(Path.Combine(UiPrefs.WorkspaceDir(), "a2a"));
        var sender = new DailySalesReportSender(host, directory);
        var result = await sender.SendDailyReportAsync(
            DateOnly.FromDateTime(DateTime.Now),
            new DailySalesSummary
            {
                SalesAmount = salesAmount,
                OrderCount = orderCount,
                RefundAmount = refundAmount,
                NewCustomerCount = newCustomerCount
            });
        var receipt = JsonSerializer.Deserialize<DailySalesReportReceipt>(result.ManagerReceiptText);
        return receipt is { Status: DailyReportReceiptStatus.Received }
            ? $"✅ 日报已送达主管 {receipt.ReceiverDisplayName}（reportId {result.Report.ReportId[..8]}，revision {result.Report.Revision}）。"
            : $"⚠️ 日报被拒绝：{string.Join("；", receipt?.Errors ?? ["未知错误"])}";
    }

    private static List<(string Name, string Icon, bool Online)>? _mentionCache;
    private static DateTimeOffset _mentionCacheAt = DateTimeOffset.MinValue;

    /// <summary>输入框 @ 选择器数据源：已配对 Helper 的名称、图标与在线状态。
    /// 短缓存避免每次击键都扫节点库；A2A 未启动或枚举失败时返回空表（弹层只剩 @全部）。</summary>
    public static List<(string Name, string Icon, bool Online)> GetMentionablePeers()
    {
        if (_mentionCache is not null && DateTimeOffset.Now - _mentionCacheAt < TimeSpan.FromSeconds(2))
        {
            return _mentionCache;
        }
        var list = new List<(string, string, bool)>();
        try
        {
            var host = App.A2A?.Controller?.Host;
            if (host is not null)
            {
                var presences = host.Presence.GetRemotePresences();
                var peers = host.Store.GetPeersAsync().GetAwaiter().GetResult()
                    .Where(p => p.TrustState == PeerTrustState.Trusted)
                    .OrderBy(p => p.DisplayName ?? p.NodeId, StringComparer.OrdinalIgnoreCase);
                foreach (var p in peers)
                {
                    presences.TryGetValue(p.NodeId, out var presence);
                    var online = presence?.Availability is PresenceAvailability.Online or PresenceAvailability.Away;
                    list.Add((p.DisplayName ?? p.NodeId, p.Icon ?? "🤖", online));
                }
            }
        }
        catch
        {
            // A2A 未启动/节点库不可读：@ 列表回退为空
        }
        _mentionCache = list;
        _mentionCacheAt = DateTimeOffset.Now;
        return list;
    }

    private A2APeerItem? FindPeer(A2AExtensionController c, string peer, bool requireTrusted)
    {
        var host = c.Host;
        if (host is null || string.IsNullOrWhiteSpace(peer)) return null;
        var peers = host.Store.GetPeersAsync().GetAwaiter().GetResult();
        var matched = peers.FirstOrDefault(p => p.NodeId.Equals(peer, StringComparison.OrdinalIgnoreCase))
            ?? peers.FirstOrDefault(p => p.NodeId.StartsWith(peer, StringComparison.OrdinalIgnoreCase))
            ?? peers.FirstOrDefault(p => (p.DisplayName ?? "").Contains(peer, StringComparison.OrdinalIgnoreCase));
        if (matched is null) return null;
        if (requireTrusted && matched.TrustState != PeerTrustState.Trusted) return null;
        return new A2APeerItem(matched, "未知", "平静");
    }

    /// <summary>用最新发现结果刷新目标节点的端点（新端点置顶），避免反复撞已失效的陈旧端口。</summary>
    private static void RefreshEndpointsFromDiscovery(A2AExtensionController c, PeerNode peer)
    {
        var host = c.Host;
        if (host is null) return;
        foreach (var ep in host.Discovery.GetDiscovered())
        {
            if (!string.Equals(ep.NodeId, peer.NodeId, StringComparison.Ordinal)) continue;
            var fresh = ep.Endpoint.TrimEnd('/') + "/";
            if (IsLoopbackEndpoint(fresh)) continue;
            peer.Endpoints.RemoveAll(x => x.Equals(fresh, StringComparison.OrdinalIgnoreCase));
            peer.Endpoints.Insert(0, fresh);
        }
    }

    /// <summary>回环地址端点只对同机实例有意义；远端节点广播回环地址说明其版本过旧或配置异常。</summary>
    private static bool IsLoopbackEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return true;
        var host = uri.Host;
        return host is "127.0.0.1" or "localhost" or "[::1]" or "::1" or "" || host.StartsWith("[::1]", StringComparison.Ordinal);
    }

    private static A2APeerItem returnNotFound(string peer) =>
        throw new InvalidOperationException($"未找到匹配 \"{peer}\" 的 Helper。请先 a2a_list_peers 查看已发现的节点（未配对的需先 a2a_pair）。");

    private A2AExtensionController RequireController()
    {
        var c = _controller();
        if (c is null || !c.IsRunning)
        {
            throw new InvalidOperationException("A2A 扩展尚未启动：请先在 A2A 会话中心确认扩展已开启。");
        }
        return c;
    }

    internal static async Task<string> GetOrCreatePeerSessionIdAsync(A2A.Transport.Hosting.A2AHost host, string selfNodeId, string peerNodeId)
    {
        var sessions = await host.Store.GetSessionsAsync();
        var existing = sessions.FirstOrDefault(s => s.Kind == SessionKind.Peer &&
            s.Members.Any(m => m.NodeId == peerNodeId));
        if (existing is not null) return existing.SessionId;
        var session = A2ASession.CreatePeer(Guid.CreateVersion7().ToString("N"), selfNodeId, peerNodeId);
        await host.Store.SaveSessionAsync(session);
        return session.SessionId;
    }

    private static string ShortId(string id) => id.Length <= 16 ? id : id[..16] + "…";
}
