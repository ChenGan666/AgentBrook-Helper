using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Im.Weixin;

/// <summary>
/// 通道配置（configJson；baseUrl 必填——生产 https://ilinkai.weixin.qq.com，测试指本地模拟服务器）。
/// accountId = 登录响应的 ilink_bot_id（入站消息的 AccountId；宿主在扫码登录后写入）。
/// </summary>
public sealed record WeixinChannelConfig(
    string BaseUrl,
    string SecretRef,
    string AccountId,
    int PollTimeoutSeconds = 35,                // 服务端 hold 约 35s；响应 longpolling_timeout_ms 可覆盖
    int BusyRetryMilliseconds = 10000,
    int SendTimeoutSeconds = 30,
    int GlobalMinIntervalMilliseconds = 1100)   // 单聊保守间隔（协议未公开限额，取 Telegram 同量级）
{
    public static WeixinChannelConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var baseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString() : null;
        var secretRef = root.TryGetProperty("secretRef", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                $"[{ImErrorCodes.BadPayload}] 缺少合法的 baseUrl（iLink 业务基地址必须显式配置，不提供默认真实域名）。");
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new InvalidOperationException($"[{ImErrorCodes.BadPayload}] 缺少 secretRef（bot token 凭据引用）。");
        return new WeixinChannelConfig(
            baseUrl.TrimEnd('/'), secretRef,
            root.TryGetProperty("accountId", out var a) ? a.GetString() ?? "" : "",
            root.TryGetProperty("pollTimeoutSeconds", out var p) ? p.GetInt32() : 35,
            root.TryGetProperty("busyRetryMilliseconds", out var br) ? br.GetInt32() : 10000,
            root.TryGetProperty("sendTimeoutSeconds", out var st) ? st.GetInt32() : 30,
            root.TryGetProperty("globalMinIntervalMilliseconds", out var g) ? g.GetInt32() : 1100);
    }
}

/// <summary>
/// 微信 iLink（ClawBot）通道：长轮询收消息 + sendmessage 投递。
/// 与 Telegram 适配器同构：游标（get_updates_buf）持久化、Busy 背压不推进、失败退避、
/// token 失效（-14）转"需重新扫码"明确失败（不静默重试，避免无意义刷服务端）。
/// </summary>
public sealed class WeixinChannel : IImChannel, ICapabilityInstance
{
    private readonly IExtensionContext _services;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, string> _contextTokens = [];      // 对端用户 → context_token
    private readonly Dictionary<string, DateTimeOffset> _lastChatSend = [];
    private readonly object _gate = new();
    private Task? _pollTask;
    private WeixinChannelConfig _config = new("http://127.0.0.1:1", "unset", "");
    private TimeProvider Clock { get; }
    private string _cursor = "";
    private int _pollRounds;                                          // get_updates_buf（必须持久化）
    private int _pollTimeoutMs;
    private DateTimeOffset _lastAnySend = DateTimeOffset.MinValue;
    private volatile ImChannelRunState _state = ImChannelRunState.Stopped;
    private volatile bool _stopped;

    public WeixinChannel(InstanceRef instance, ActivationId activation, IExtensionContext services,
        string configJson, TimeProvider? clock = null)
    {
        Instance = instance;
        Activation = activation;
        _services = services;
        Clock = clock ?? TimeProvider.System;
        try { _config = WeixinChannelConfig.Parse(configJson); }
        catch (Exception) { /* 工厂以空配置构造；StartAsync 权威解析 */ }
        _pollTimeoutMs = _config.PollTimeoutSeconds * 1000;
    }

    public InstanceRef Instance { get; }
    public ActivationId Activation { get; }
    public string ChannelId => "weixin";
    public string DisplayName => "WeChat";
    public bool IsRunning => _state is ImChannelRunState.Running or ImChannelRunState.Degraded;

    /// <summary>微信单条 2048 字符（协议事实）；client_id 即幂等键 → Unknown 可安全重发。</summary>
    public ImChannelCapabilities Capabilities { get; } = new(
        MaxTextChars: 2048, MaxOutFileBytes: 50L * 1024 * 1024, MaxInFileBytes: 50L * 1024 * 1024,
        MaxFilesPerMessage: 5, SupportsCaption: false, CaptionMaxChars: 0,
        MarkdownMode: "None", ReplyWindow: TimeSpan.FromMinutes(2), GroupRatePerMinute: 18,
        HasOutboundIdempotencyKey: true);

    public event Action<ImChannelStatus>? StatusChanged;
    internal IImInboundSink? Sink { get; private set; }

    /// <summary>诊断日志：写宿主审计文件（im/audit.jsonl，用户可直接查看）+ stderr。</summary>
    private void Log(string message)
    {
        try { _services.Log(message); } catch { /* 宿主不支持则忽略 */ }
        try { Console.Error.WriteLine(message); } catch { }
    }

    private void SetState(ImChannelRunState state, string? reason = null)
    {
        _state = state;
        if (reason is not null || state is ImChannelRunState.Failed or ImChannelRunState.Degraded)
            Log($"[wx] 状态：{state}{(string.IsNullOrEmpty(reason) ? "" : " · " + reason)}");
        StatusChanged?.Invoke(new ImChannelStatus(state, reason, Clock.GetUtcNow()));
    }

    // ── 生命周期 ──

    public async Task StartAsync(ImChannelStartContext context, CancellationToken cancellationToken)
    {
        _config = WeixinChannelConfig.Parse(context.ConfigJson);
        Sink = context.Sink;
        _pollTimeoutMs = _config.PollTimeoutSeconds * 1000;
        SetState(ImChannelRunState.Starting);

        // 凭据服务可用性前置校验（token 由宿主持有；缺失注入会在首请求暴露）
        _ = _services.GetService<ICredentialService>()
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ServiceNotGranted}] 凭据服务未授予。");
        _ = _services.GetService<INetworkService>()
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ServiceNotGranted}] 网络服务未授予。");

        // 游标与 context_token 恢复（协议态归插件命名空间存储）
        var stateStore = _services.GetService<IPluginStateStore>();
        if (stateStore is not null)
        {
            var savedCursor = await stateStore.GetAsync("get_updates_buf", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(savedCursor)) _cursor = savedCursor;
            var savedTokens = await stateStore.GetAsync("context_tokens", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(savedTokens))
            {
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(savedTokens);
                    if (map is not null)
                        foreach (var (k, v) in map) _contextTokens[k] = v;
                }
                catch (JsonException) { /* 损坏则丢弃，随消息重建 */ }
            }
        }

        // 生命周期通知（最佳努力但记录结果：这是"token 是否被服务端接受"的第一个可判据）
        _ = Task.Run(async () =>
        {
            try
            {
                var http = await SendPostAsync(WeixinProtocol.PathNotifyStart, new { },
                    TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                if (http is null) { Log("[wx] notifystart 无响应（网络/超时）"); return; }
                Log($"[wx] notifystart HTTP {http.StatusCode}：{Preview(Encoding.UTF8.GetString(http.Body))}");
            }
            catch (Exception ex) { Log($"[wx] notifystart 异常：{ex.Message}"); }
        }, CancellationToken.None);

        SetState(ImChannelRunState.Running);
        Log($"[wx] 插件版本：1.0.1-media（含入站媒体解析；未见此标记=旧 DLL）");
        Log($"[wx] 通道启动：baseUrl={_config.BaseUrl} account={_config.AccountId} " +
                                $"cursor={(string.IsNullOrEmpty(_cursor) ? "(空)" : _cursor[..Math.Min(8, _cursor.Length)])}");
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public async Task StopAsync(TimeSpan gracePeriod)
    {
        try
        {
            _stopped = true;
            _cts.Cancel();
        if (_pollTask is not null)
        {
            var finished = await Task.WhenAny(_pollTask, Task.Delay(gracePeriod)).ConfigureAwait(false);
            if (finished != _pollTask)
            {
                SetState(ImChannelRunState.Failed, "停止超时：长轮询未在宽限期内退出");
                return;
            }
        }
            try { await SendPostAsync(WeixinProtocol.PathNotifyStop, new { }, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
            catch { /* 最佳努力 */ }
        }
        catch (Exception ex)
        {
            Log($"[wx] 停止时异常（已忽略）：{ex.Message}");
        }
        finally
        {
            SetState(ImChannelRunState.Stopped);
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopped = true;      // 立即让轮询循环在下一轮退出（不等取消令牌传播）
        _cts.Cancel();
        SetState(ImChannelRunState.Stopped);
        return ValueTask.CompletedTask;
    }

    // ── 轮询循环 ──

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            await PollLoopCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 停止/卸载：正常退出 */ }
        catch (Exception ex)
        {
            Log($"[wx] 轮询循环异常退出：{ex.Message}");
            SetState(ImChannelRunState.Failed, $"轮询异常退出：{ex.Message}");
        }
    }

    private async Task PollLoopCoreAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested && !_stopped)
        {
            JsonElement? json = null;
            var roundSucceeded = false;
            try
            {
                var body = WeixinProtocol.WithBaseInfo(new { get_updates_buf = _cursor });
                var http = await SendPostAsync(WeixinProtocol.PathGetUpdates, body,
                    TimeSpan.FromMilliseconds(_pollTimeoutMs + 15000), ct).ConfigureAwait(false);

                if (http is null) { continue; }   // 传输超时/断连：长轮询正常控制流（保留游标）

                if (!http.IsSuccess)
                {
                    SetState(ImChannelRunState.Degraded, $"getupdates HTTP {http.StatusCode}");
                    backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                try { using var doc = JsonDocument.Parse(http.Body); json = doc.RootElement.Clone(); }
                catch (JsonException)
                {
                    SetState(ImChannelRunState.Degraded, "getupdates 响应不可解析");
                    backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                var ret = WeixinProtocol.ReadRet(json.Value);
                var round = Interlocked.Increment(ref _pollRounds);
                var rawBody = json.Value.GetRawText();
                var hasMsgsInBody = rawBody.Contains("\"msgs\":[{" , StringComparison.Ordinal);
                if (round <= 3 || hasMsgsInBody)
                    Log($"[wx] getupdates#{round} ret={ret}（{rawBody.Length} 字节）：{Preview(rawBody)}");
                else
                    Log($"[wx] getupdates ret={ret}（{rawBody.Length} 字节）");
                if (ret == WeixinProtocol.ErrTokenInvalid)
                {
                    // 明确失败：需用户在 Helper 重新扫码（不静默重试）
                    SetState(ImChannelRunState.Failed, "微信登录已失效（-14），请在设置 → IM 连接中重新扫码登录");
                    return;
                }
                if (ret != 0)
                {
                    Log($"[wx] getupdates 失败响应：{Preview(json.Value.GetRawText())}");
                    SetState(ImChannelRunState.Degraded, $"getupdates ret={ret}");
                    backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                backoff = TimeSpan.FromSeconds(1);
                if (_state != ImChannelRunState.Running) SetState(ImChannelRunState.Running);
                roundSucceeded = true;

                if (json.Value.TryGetProperty("longpolling_timeout_ms", out var lp)
                    && lp.ValueKind == JsonValueKind.Number && lp.GetInt32() > 0)
                {
                    _pollTimeoutMs = lp.GetInt32();
                }

                var busy = false;
                if (json.Value.TryGetProperty("msgs", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    if (msgs.GetArrayLength() > 0)
                        Log($"[wx] getupdates 收到 {msgs.GetArrayLength()} 条");
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        // 停止/重绑取消时**不得丢弃已拉到的消息**：服务端已把该批投递给我们并推进会话游标，
                        // 丢弃即永久丢失（新实例用旧游标也拉不回来）。已到手的这一批照常入站，再做退出判断。
                        var inbound = Normalize(msg);
                        if (inbound is null)
                        {
                            Log($"[wx] 消息被忽略（无法归一化）：{Preview(msg.GetRawText())}");
                            continue;
                        }
                        Log($"[wx] 入站开始：from={inbound.SenderId} chat={inbound.ChatId} " +
                            $"account={inbound.AccountId} text={Truncate(inbound.Text)} sink={(Sink is null ? "NULL" : "ok")}");
                        ImAcceptResult accept;
                        try
                        {
                            // 10s 超时，且**不链接通道取消令牌**（取消不得阻断已到手消息的入站）
                            using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            accept = await Sink!.AcceptInboundAsync(inbound, acceptCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"[wx] 入站异常：{ex.GetType().Name}: {ex.Message}");
                            throw;
                        }
                        Log(accept.IsDecided
                            ? $"[wx] 入站已决：{accept.State}"
                              + (string.IsNullOrEmpty(accept.ReasonCode) ? "" : $"（{accept.ReasonCode}）")
                              + $"（from={inbound.SenderId} text={Truncate(inbound.Text)}）"
                            : $"[wx] 入站未决（Busy）：{inbound.SenderId}");
                        if (!accept.IsDecided)
                        {
                            // Busy：不推进游标（服务端保留未确认更新），背压暂停本批
                            SetState(ImChannelRunState.Degraded, "Inbox Busy：暂停拉取（不推进游标）");
                            await PersistStateAsync(ct).ConfigureAwait(false);
                            busy = true;
                            break;
                        }
                    }
                }

                // 仅在成功且非 Busy 时推进游标（协议 §2.6.1：新游标非空才替换并持久化）
                if (!busy
                    && json.Value.TryGetProperty("get_updates_buf", out var nb)
                    && nb.ValueKind == JsonValueKind.String)
                {
                    var next = nb.GetString();
                    if (!string.IsNullOrEmpty(next) && next != _cursor)
                    {
                        _cursor = next;
                        // 用非取消令牌持久化：避免"消息已入站但游标没落盘"导致下轮重复拉取
                        await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }

                if (ct.IsCancellationRequested || _stopped)
                {
                    Log("[wx] 收到停止信号：本批已处理完，退出轮询");
                    return;
                }
                if (busy)
                {
                    await DelaySafeAsync(TimeSpan.FromMilliseconds(_config.BusyRetryMilliseconds), ct).ConfigureAwait(false);
                }
                else if (!json.Value.TryGetProperty("msgs", out var emptyCheck) || emptyCheck.GetArrayLength() == 0)
                {
                    await DelaySafeAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);   // 空批让出
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"[wx] 轮询异常：{ex.GetType().Name}: {ex.Message}");
                SetState(ImChannelRunState.Degraded, $"轮询异常：{ex.Message}");
                try { backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            finally
            {
                _ = roundSucceeded;   // 预留：成功轮次统计
            }
        }
    }

    private static async Task<TimeSpan> BackoffAsync(TimeSpan current, CancellationToken ct)
    {
        await DelaySafeAsync(current, ct).ConfigureAwait(false);
        return current.TotalSeconds switch
        {
            <= 1 => TimeSpan.FromSeconds(2),
            <= 2 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(15),
        };
    }

    private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    // ── 入站归一化 ──

    /// <summary>iLink 消息信封 → 统一入站消息（P1-a：仅文本；媒体 P1-b）。</summary>
    internal ImInboundMessage? Normalize(JsonElement msg)
    {
        var fromUserId = msg.TryGetProperty("from_user_id", out var f) ? f.GetString() : null;
        if (string.IsNullOrWhiteSpace(fromUserId)) return null;

        var messageType = msg.TryGetProperty("message_type", out var mt) && mt.ValueKind == JsonValueKind.Number
            ? mt.GetInt32() : 1;   // 1=USER 2=BOT
        var isBot = messageType == 2;
        var isSelf = isBot || (!string.IsNullOrEmpty(_config.AccountId)
            && string.Equals(fromUserId, _config.AccountId, StringComparison.Ordinal));

        var text = "";
        var hasUnsupportedItem = false;
        List<ImAttachmentDescriptor>? attachments = null;
        string? voiceTranscript = null;          // B11：平台转写文本（voice_item.text；多条语音取最后一条，罕见）
        bool adoptedIntoText = false;            // R1 方案 A：本次采纳动作是否填过 text（决定是否清空）
        var pendingRejectedVoice = new List<int>();  // B8：无下载参数的语音项索引 → 合成 Rejected 描述符
        var itemIndex = -1;
        if (msg.TryGetProperty("item_list", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                itemIndex++;
                var type = item.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.Number ? ty.GetInt32() : 0;
                if (type == 1)
                {
                    if (item.TryGetProperty("text_item", out var ti) && ti.TryGetProperty("text", out var tx))
                        text = tx.GetString() ?? "";
                    continue;
                }
                if (type == 3)
                {
                    // 语音：平台转写文本（doc/Voice B11，第五轮 R1 + 第六轮混合消息边界）——
                    // voice_item.text 存在即记入描述符 Transcript（与 text 是否为空无关）；仅当本次
                    // 采纳动作填过 text 时才清空（文本单通道注入，防 B9 修复后 userText 双份）
                    if (item.TryGetProperty("voice_item", out var vi) && vi.TryGetProperty("text", out var vt)
                        && !string.IsNullOrWhiteSpace(vt.GetString()))
                    {
                        voiceTranscript = vt.GetString();
                        if (string.IsNullOrEmpty(text))
                        {
                            text = voiceTranscript;
                            adoptedIntoText = true;
                        }
                    }
                }
                if (type != 1)
        {
            var keys = item.ValueKind == JsonValueKind.Object
                ? string.Join(",", item.EnumerateObject().Select(pr => pr.Name))
                : item.ValueKind.ToString();
            Log($"[wx] 媒体项 type={type} 字段=[{keys}]");
        }
        var descriptor = BuildMediaDescriptor(type, item);
                if (type == 3)
                {
                    // B8（doc/Voice/01）：无下载参数的语音不再落 hasUnsupportedItem（通用"附件失败"）——
                    // 合成 State=Rejected 描述符（RemoteId 用消息项 id），执行器走确定性回音 ui.voice.unavailable
                    if (descriptor is null)
                    {
                        pendingRejectedVoice.Add(itemIndex);
                        continue;   // 跳过统一入列（下方 messageId 解析后合成）
                    }
                    // B11：平台转写挂描述符（Engine="platform"）——执行器单一判定"Transcript 非空=免转写直用"
                    if (voiceTranscript is not null)
                        descriptor = descriptor with { Transcript = new ImAttachmentTranscript { Text = voiceTranscript, Engine = "platform" } };
                }
                if (descriptor is not null) (attachments ??= []).Add(descriptor);
                else hasUnsupportedItem = true;
            }
        }
        // 纯媒体消息（无文本、但带附件）：仍要入站（上层能力检查/执行器会带图处理）
        if (text.Length == 0 && attachments is not { Count: > 0 } && !hasUnsupportedItem)
            return null;   // 真正空内容（如心跳/已删除）：忽略

        var contextToken = msg.TryGetProperty("context_token", out var ctEl) ? ctEl.GetString() : null;
        if (!string.IsNullOrEmpty(contextToken))
        {
            lock (_gate) _contextTokens[fromUserId] = contextToken!;
        }

        var messageId = msg.TryGetProperty("message_id", out var mid)
            ? (mid.ValueKind == JsonValueKind.Number ? mid.GetRawText() : mid.GetString() ?? "")
            : (msg.TryGetProperty("client_id", out var cid) ? cid.GetString() ?? "" : "");
        if (string.IsNullOrEmpty(messageId))
            messageId = Guid.NewGuid().ToString("N");

        // B8（doc/Voice/01）：无下载参数语音的合成 Rejected 描述符（messageId 已有 Guid 兜底，IsValid 仅要求非空）
        foreach (var idx in pendingRejectedVoice)
            (attachments ??= []).Add(new ImAttachmentDescriptor(WeixinMedia.Provider, 1,
                $"voice-{messageId}-{idx}", "voice.silk", "audio/silk",
                ImAttachmentKind.Voice, null, null, ImAttachmentState.Rejected, null));

        // R1 方案 A（doc/Voice/01 B11）：平台转写文本只走 Transcript 通道——本次采纳填过 text 则清空，
        // 防 B9 修复后 userText 与 [语音] 注入双份进模型输入
        if (voiceTranscript is not null && adoptedIntoText)
            text = "";

        var timestamp = Clock.GetUtcNow();
        if (msg.TryGetProperty("create_time_ms", out var ctm) && ctm.ValueKind == JsonValueKind.Number)
        {
            try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ctm.GetInt64()); }
            catch (ArgumentOutOfRangeException) { /* 异常时间戳回退当前 */ }
        }

        return new ImInboundMessage(
            ChannelId, Instance.InstanceId, _config.AccountId,
            ChatId: fromUserId,
            ChatType: ImChatType.Private,
            SenderId: fromUserId,
            SenderName: null,
            IsSelf: isSelf,
            IsBot: isBot,
            Text: text,
            MessageId: messageId,
            Timestamp: timestamp,
            Attachments: attachments);   // 媒体：CDN + AES-128-ECB（MaterializeAsync 下载解密）
    }

    // ── 出站投递 ──

    public async Task<DeliveryResult> SubmitDeliveryAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        var text = request.Text ?? "";
        var hasFiles = request.Files is { Count: > 0 };
        if (text.Length == 0 && !hasFiles)
            return DeliveryResult.Rejected(ImErrorCodes.BadPayload, "空消息不发送。");
        if (text.Length > Capabilities.MaxTextChars)
            return DeliveryResult.Rejected(ImErrorCodes.BadPayload,
                $"文本超长（{text.Length} > {Capabilities.MaxTextChars}）；应由管道分片。");

        var contextToken = ResolveContextToken(request.ChatId);
        if (string.IsNullOrEmpty(contextToken))
            return DeliveryResult.Rejected(ImErrorCodes.Permanent,
                "缺少 context_token（该用户尚未发送入站消息；微信不允许主动开启会话）。");

        // 出站媒体：先各发一条媒体消息（协议建议文本与媒体分开、每条 item_list 只放一个 item）
        var fileResults = new List<DeliveryResult>();
        if (hasFiles)
        {
            var blobs = _services.GetService<IBlobService>();
            if (blobs is null)
                return DeliveryResult.Rejected(ImErrorCodes.ServiceNotGranted, "Blob 服务未授予，文件发送不可用。");
            foreach (var file in request.Files!)
            {
                var sent = await SendFileAsync(request, file, blobs, contextToken, cancellationToken).ConfigureAwait(false);
                fileResults.Add(sent);
                if (sent.State is DeliveryState.Rejected) return sent;
            }
            if (string.IsNullOrWhiteSpace(text))
                return fileResults[^1];   // 纯文件：以最后一条结果为准
        }

        await ThrottleAsync(request.ChatId, cancellationToken).ConfigureAwait(false);

        // client_id = 幂等键（协议要求每次唯一，可作本地消息 ID）→ 有幂等键，Unknown 可安全重发
        var clientId = "ab-" + (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey);

        var payload = WeixinProtocol.WithBaseInfo(new
        {
            msg = new
            {
                from_user_id = "",
                to_user_id = request.ChatId,
                client_id = clientId,
                message_type = 2,
                message_state = 2,
                context_token = contextToken,
                item_list = new object[] { new { type = 1, text_item = new { text } } },
            },
        });

        try
        {
            var http = await SendPostAsync(WeixinProtocol.PathSendMessage, payload,
                TimeSpan.FromSeconds(_config.SendTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            if (http is null)
                return DeliveryResult.Unknown("sendmessage 网络失败：结果未知（幂等键可安全重发）");
            return InterpretSendResponse(http, clientId);
        }
        catch (Exception ex)
        {
            // 含取消（服务停止/卸载）：统一报"结果未知"，由 Outbox 按幂等键决定是否重发
            return DeliveryResult.Unknown($"sendmessage 未完成：{ex.Message}（结果未知）");
        }
    }

    /// <summary>
    /// 出站媒体（协议 §2.9.1）：读 Blob → AES-128-ECB 加密 → getuploadurl → CDN 上传（取响应头
    /// x-encrypted-param）→ sendmessage 媒体项（图片 image_item / 文件 file_item）。
    /// </summary>
    private async Task<DeliveryResult> SendFileAsync(DeliveryRequest request, ContentRef file,
        IBlobService blobs, string contextToken, CancellationToken ct)
    {
        try
        {
            await using var stream = await blobs.OpenReadAsync(file, "outbound-delivery", ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var plain = ms.ToArray();
            if (plain.Length == 0)
                return DeliveryResult.Rejected(ImErrorCodes.Permanent, $"文件为空：{file.FileName}");

            var isImage = file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
            var isVideo = file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
            var mediaType = isImage ? 1 : isVideo ? 2 : 3;          // 1=IMAGE 2=VIDEO 3=FILE
            var aesKey = RandomNumberGenerator.GetBytes(16);
            var fileKey = RandomNumberGenerator.GetBytes(16);
            var cipher = WeixinMedia.EncryptAesEcb(plain, aesKey);
            var md5 = Convert.ToHexString(MD5.HashData(plain)).ToLowerInvariant();

            // ① 上传凭据
            var uploadReq = WeixinProtocol.WithBaseInfo(new
            {
                filekey = Convert.ToHexString(fileKey).ToLowerInvariant(),
                media_type = mediaType,
                to_user_id = request.ChatId,
                rawsize = plain.Length,
                rawfilemd5 = md5,
                filesize = cipher.Length,
                no_need_thumb = true,
                aeskey = Convert.ToHexString(aesKey).ToLowerInvariant(),
            });
            var uploadHttp = await SendPostAsync(WeixinProtocol.PathGetUploadUrl, uploadReq,
                TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (uploadHttp is null || !uploadHttp.IsSuccess)
                return DeliveryResult.Rejected(ImErrorCodes.Temporary,
                    $"getuploadurl 失败：{uploadHttp?.StatusCode.ToString() ?? "无响应"}");
            string? uploadUrl = null;
            using (var doc = JsonDocument.Parse(uploadHttp.Body))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("upload_full_url", out var fu) && !string.IsNullOrWhiteSpace(fu.GetString()))
                    uploadUrl = fu.GetString();
                else if (root.TryGetProperty("upload_param", out var up))
                {
                    var param = up.GetString() ?? "";
                    uploadUrl = $"{WeixinMedia.CdnBase}/upload?encrypted_query_param={Uri.EscapeDataString(param)}"
                              + $"&filekey={Convert.ToHexString(fileKey).ToLowerInvariant()}";
                }
                if (WeixinProtocol.ReadRet(root) != 0)
                    return DeliveryResult.Rejected(ImErrorCodes.Temporary, "getuploadurl 返回 ret≠0");
            }
            if (string.IsNullOrEmpty(uploadUrl))
                return DeliveryResult.Rejected(ImErrorCodes.Temporary, "getuploadurl 未返回上传地址");

            // ② 上传密文（Content-Type: application/octet-stream）
            var upload = await SendRawAsync(HttpMethod.Post, uploadUrl,
                [new HttpHeader("Content-Type", "application/octet-stream")], cipher,
                TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (upload is null || !upload.IsSuccess)
                return DeliveryResult.Rejected(ImErrorCodes.Temporary,
                    $"CDN 上传失败：{upload?.StatusCode.ToString() ?? "无响应"}");
            var encryptParam = upload.Headers is not null
                && upload.Headers.TryGetValue("x-encrypted-param", out var ep) ? ep : null;
            if (string.IsNullOrWhiteSpace(encryptParam))
                return DeliveryResult.Rejected(ImErrorCodes.Temporary, "CDN 响应缺少 x-encrypted-param");

            // ③ 发送媒体消息（每条只放一个 item）
            var aesItemKey = WeixinMedia.ToItemAesKey(aesKey);
            object item = mediaType switch
            {
                1 => new
                {
                    type = 2,
                    image_item = new
                    {
                        media = new { encrypt_query_param = encryptParam, aes_key = aesItemKey, encrypt_type = 1 },
                        mid_size = plain.Length,
                    },
                },
                2 => new
                {
                    type = 5,
                    video_item = new
                    {
                        media = new { encrypt_query_param = encryptParam, aes_key = aesItemKey, encrypt_type = 1 },
                        video_size = plain.Length,
                    },
                },
                _ => new
                {
                    type = 4,
                    file_item = new
                    {
                        media = new { encrypt_query_param = encryptParam, aes_key = aesItemKey, encrypt_type = 1 },
                        file_name = file.FileName,
                        len = plain.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                },
            };
            var clientId = "ab-file-" + Guid.NewGuid().ToString("N");
            var body = WeixinProtocol.WithBaseInfo(new
            {
                msg = new
                {
                    from_user_id = "",
                    to_user_id = request.ChatId,
                    client_id = clientId,
                    message_type = 2,
                    message_state = 2,
                    context_token = contextToken,
                    item_list = new[] { item },
                },
            });
            var sent = await SendPostAsync(WeixinProtocol.PathSendMessage, body,
                TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (sent is null) return DeliveryResult.Unknown("sendmessage(媒体) 网络失败：结果未知");
            var result = InterpretSendResponse(sent, clientId);
            Log($"[wx] 出站媒体：{file.FileName}（{(isImage ? "图片" : isVideo ? "视频" : "文件")} {plain.Length} 字节）→ {result.State}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"[wx] 出站媒体异常：{ex.GetType().Name}: {ex.Message}");
            return DeliveryResult.Rejected(ImErrorCodes.Temporary, $"媒体发送异常：{ex.Message}");
        }
    }

    /// <summary>RAW 请求（自定义头 + 字节体；不传取消令牌，理由同 SendPostAsync）。</summary>
    private async Task<HttpResult?> SendRawAsync(HttpMethod method, string url, IReadOnlyList<HttpHeader> headers,
        byte[] body, TimeSpan timeout, CancellationToken ct)
    {
        var network = _services.GetService<INetworkService>();
        if (network is null) return null;
        try
        {
            var sendTask = network.SendAsync(new HttpRequestSpec(method, url, headers, body, [], timeout),
                CancellationToken.None);
            var finished = await Task.WhenAny(sendTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (finished != sendTask)
            {
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                return null;
            }
            return await sendTask.ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>sendmessage 响应判定（协议 §2.6.4：HTTP 成功 + ret=0/缺省才算成功）。</summary>
    internal static DeliveryResult InterpretSendResponse(HttpResult http, string clientId)
    {
        if (http.StatusCode == 429)
            return DeliveryResult.Rejected(ImErrorCodes.Temporary, "sendmessage 被限流（HTTP 429）")
                with { RetryAfter = TimeSpan.FromSeconds(5) };
        if (!http.IsSuccess)
            return http.StatusCode is 400 or 401 or 403 or 404
                ? DeliveryResult.Rejected(ImErrorCodes.Permanent, $"sendmessage HTTP {http.StatusCode}")
                : DeliveryResult.Rejected(ImErrorCodes.Temporary, $"sendmessage HTTP {http.StatusCode}");

        try
        {
            using var doc = JsonDocument.Parse(http.Body);
            var ret = WeixinProtocol.ReadRet(doc.RootElement);
            if (ret == WeixinProtocol.ErrTokenInvalid)
                return DeliveryResult.Rejected(ImErrorCodes.Unauthorized, "微信登录已失效（-14），需重新扫码");
            if (ret != 0)
                return DeliveryResult.Rejected(ImErrorCodes.Temporary, $"sendmessage ret={ret}")
                    with { RetryAfter = TimeSpan.FromSeconds(5) };
            return DeliveryResult.PlatformAccepted(clientId);
        }
        catch (JsonException)
        {
            return DeliveryResult.Unknown("sendmessage 响应不可解析（结果未知）");
        }
    }

    private async Task ThrottleAsync(string chatId, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = Clock.GetUtcNow();
            var interval = TimeSpan.FromMilliseconds(_config.GlobalMinIntervalMilliseconds);
            var globalWait = _lastAnySend == DateTimeOffset.MinValue ? TimeSpan.Zero : interval - (now - _lastAnySend);
            var chatWait = _lastChatSend.TryGetValue(chatId, out var last) ? interval - (now - last) : TimeSpan.Zero;
            wait = globalWait > chatWait ? globalWait : chatWait;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            _lastAnySend = now + wait;
            _lastChatSend[chatId] = now + wait;
        }
        await DelaySafeAsync(wait, ct).ConfigureAwait(false);
    }

    private string? ResolveContextToken(string chatId)
    {
        lock (_gate) return _contextTokens.TryGetValue(chatId, out var v) ? v : null;
    }

    /// <summary>媒体 item → 统一附件描述符（type 2=图片 3=语音 4=文件 5=视频）。</summary>
    private ImAttachmentDescriptor? BuildMediaDescriptor(int type, JsonElement item)
    {
        var (container, kind, defaultName) = type switch
        {
            2 => ("image_item", ImAttachmentKind.Image, "image.jpg"),
            3 => ("voice_item", ImAttachmentKind.Voice, "voice.silk"),
            4 => ("file_item", ImAttachmentKind.File, "file.bin"),
            5 => ("video_item", ImAttachmentKind.Video, "video.mp4"),
            _ => ("", ImAttachmentKind.File, ""),
        };
        if (container.Length == 0) return null;
        if (!item.TryGetProperty(container, out var node))
        {
            Log($"[wx] 媒体项缺少容器 {container}（实际字段见上一条日志）");
            return null;
        }

        var media = node.TryGetProperty("media", out var m) ? m : default;
        var fullUrl = media.ValueKind == JsonValueKind.Object && media.TryGetProperty("full_url", out var fu)
            ? fu.GetString() ?? "" : "";
        var query = media.ValueKind == JsonValueKind.Object && media.TryGetProperty("encrypt_query_param", out var eq)
            ? eq.GetString() ?? "" : "";
        var mediaKey = media.ValueKind == JsonValueKind.Object && media.TryGetProperty("aes_key", out var ak)
            ? ak.GetString() ?? "" : "";
        // 图片另有 image_item.aeskey（hex）——media.aes_key 缺失时用它
        if (string.IsNullOrEmpty(mediaKey) && node.TryGetProperty("aeskey", out var ik))
            mediaKey = ik.GetString() ?? "";
        if (string.IsNullOrEmpty(fullUrl) && string.IsNullOrEmpty(query))
        {
            Log($"[wx] 媒体项无可下载参数（full_url/encrypt_query_param 均空；media 字段="
                + (media.ValueKind == JsonValueKind.Object
                    ? string.Join(",", media.EnumerateObject().Select(pr => pr.Name)) : media.ValueKind.ToString()) + "）");
            return null;
        }

        var fileName = node.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
        long? size = type switch
        {
            2 => node.TryGetProperty("mid_size", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt64() : null,
            4 => node.TryGetProperty("len", out var ln)
                ? (ln.ValueKind == JsonValueKind.Number ? ln.GetInt64()
                    : long.TryParse(ln.GetString(), out var parsed) ? parsed : null)
                : null,
            5 => node.TryGetProperty("video_size", out var vs) && vs.ValueKind == JsonValueKind.Number ? vs.GetInt64() : null,
            _ => null,
        };
        var mediaType = type switch
        {
            2 => "image/jpeg",
            3 => "audio/silk",
            4 => "application/octet-stream",
            5 => "video/mp4",
            _ => null,
        };

        var reference = new WeixinMedia.MediaRef(fullUrl, query, mediaKey, fileName ?? defaultName, size);
        return new ImAttachmentDescriptor(WeixinMedia.Provider, 1, WeixinMedia.Encode(reference),
            fileName ?? defaultName, mediaType, kind, size, null, ImAttachmentState.Referenced, null);
    }

    /// <summary>媒体物化：CDN 下载 → AES-128-ECB 解密 → 宿主 Blob（供多模态输入与展示）。</summary>
    public async Task<ContentRef?> MaterializeAsync(ImAttachmentDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.Provider != WeixinMedia.Provider) return null;
        var reference = WeixinMedia.Decode(descriptor.RemoteId);
        if (reference is null) { Log("[wx] 媒体引用无法解析"); return null; }
        try
        {
            var url = WeixinMedia.ResolveDownloadUrl(reference);
            var http = await SendGetAsync(url, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (http is null || !http.IsSuccess)
            {
                Log($"[wx] 媒体下载失败 HTTP {http?.StatusCode.ToString() ?? "无响应"}：{url[..Math.Min(80, url.Length)]}");
                return null;
            }
            byte[] plain = http.Body;
            if (!string.IsNullOrWhiteSpace(reference.AesKeyRaw))
                plain = WeixinMedia.DecryptAesEcb(http.Body, WeixinMedia.ResolveKey(reference.AesKeyRaw));

            var blobs = _services.GetService<IBlobService>();
            if (blobs is null) { Log("[wx] Blob 服务未授予，媒体无法落盘"); return null; }
            await using var ms = new MemoryStream(plain);
            var content = await blobs.WriteAsync("inbound-attachment", ms, Capabilities.MaxInFileBytes,
                reference.FileName ?? descriptor.Name ?? "media.bin", descriptor.MediaType, cancellationToken)
                .ConfigureAwait(false);
            Log($"[wx] 媒体物化成功：{descriptor.Kind} {plain.Length} 字节");
            return content;
        }
        catch (Exception ex)
        {
            Log($"[wx] 媒体物化异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>GET（不传取消令牌，理由同 SendPostAsync）。</summary>
    private async Task<HttpResult?> SendGetAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var network = _services.GetService<INetworkService>();
        if (network is null) return null;
        try
        {
            var sendTask = network.SendAsync(new HttpRequestSpec(
                HttpMethod.Get, url, [], null, [], timeout), CancellationToken.None);
            var finished = await Task.WhenAny(sendTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (finished != sendTask)
            {
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                return null;
            }
            return await sendTask.ConfigureAwait(false);
        }
        catch { return null; }
    }

    // ── HTTP（token 经凭据服务以 BearerHeader 注入，不落日志）──

    /// <summary>业务 POST；传输失败返回 null（调用方按场景决定：长轮询=正常控制流）。</summary>
    private async Task<HttpResult?> SendPostAsync(string path, object payload, TimeSpan timeout, CancellationToken ct)
        => await SendPostAsync(path, WeixinProtocol.WithBaseInfo(payload), timeout, ct).ConfigureAwait(false);

    private async Task<HttpResult?> SendPostAsync(string path, string body, TimeSpan timeout, CancellationToken ct)
    {
        var network = _services.GetService<INetworkService>()
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ServiceNotGranted}] 网络服务未授予。");
        var credentials = _services.GetService<ICredentialService>()
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ServiceNotGranted}] 凭据服务未授予。");

        try
        {
            // 关键：**不把取消令牌交给 HttpClient**——停止/卸载时取消长轮询会让 HttpClient 抛
            // OperationCanceledException，在开启"引发异常时中断"的调试器里会当成崩溃弹出（用户已多次遇到）。
            // 改为"竞速等待"：请求照发（内部有 timeout 收敛），我们只等到取消/超时即返回 null，
            // 放弃的请求由内部超时自行结束，其异常就地观察（不产生未观察任务异常）。
            var sendTask = network.SendAsync(new HttpRequestSpec(
                HttpMethod.Post,
                _config.BaseUrl + path,
                WeixinProtocol.PostHeaders(),
                Encoding.UTF8.GetBytes(body),
                [credentials.InjectionFor(_config.SecretRef, SecretPlacement.BearerHeader)],
                timeout), CancellationToken.None);

            var finished = await Task.WhenAny(sendTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (finished != sendTask)
            {
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                return null;   // 取消或超时：视为"无响应"（长轮询正常控制流）
            }
            return await sendTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;   // 传输失败一律消化（不穿透宿主/UI）
        }
    }

    // ── 状态持久化 ──

    private async Task PersistStateAsync(CancellationToken ct)
    {
        var stateStore = _services.GetService<IPluginStateStore>();
        if (stateStore is null) return;
        try
        {
            await stateStore.SetAsync("get_updates_buf", _cursor, ct).ConfigureAwait(false);
            string tokensJson;
            lock (_gate) tokensJson = JsonSerializer.Serialize(_contextTokens);
            await stateStore.SetAsync("context_tokens", tokensJson, ct).ConfigureAwait(false);
        }
        catch (Exception) { /* 持久化失败不阻断消息循环（下轮重试） */ }
    }

    private static string Preview(string text) =>
        text.Length > 400 ? text[..400] + "…" : text;

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        return text.Length > 24 ? text[..24] + "…" : text;
    }
}
