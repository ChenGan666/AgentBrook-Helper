using System.Text;
using System.Text.Json;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Im.Telegram;

/// <summary>通道配置（configJson 形态；apiBase 必填——生产填受审查官方域，测试填本地模拟服务器）。</summary>
public sealed record TelegramChannelConfig(
    string ApiBase,
    string SecretRef,
    int PollTimeoutSeconds = 30,
    int PollLimit = 100,
    int BusyRetryMilliseconds = 10000,
    int GlobalMinIntervalMilliseconds = 40,     // 25 msg/s 保守令牌间隔
    int ChatMinIntervalMilliseconds = 1100,     // 单聊 ≥1.1s（channel-telegram.md §3.2）
    int SendTimeoutSeconds = 30)                // 发送超时（超时=结果未知 T05）
{
    public static TelegramChannelConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var apiBase = root.TryGetProperty("apiBase", out var a) ? a.GetString() : null;
        var secretRef = root.TryGetProperty("secretRef", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(apiBase) || !Uri.TryCreate(apiBase, UriKind.Absolute, out _))
            throw new InvalidOperationException($"[{ImErrorCodes.BadPayload}] 缺少合法的 apiBase 配置（通道平台基地址必须显式配置，不提供默认真实域名）。");
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new InvalidOperationException($"[{ImErrorCodes.BadPayload}] 缺少 secretRef 配置（bot token 凭据引用）。");
        return new TelegramChannelConfig(
            apiBase.TrimEnd('/'), secretRef,
            root.TryGetProperty("pollTimeoutSeconds", out var p) ? p.GetInt32() : 30,
            root.TryGetProperty("pollLimit", out var l) ? l.GetInt32() : 100,
            root.TryGetProperty("busyRetryMilliseconds", out var b) ? b.GetInt32() : 10000,
            root.TryGetProperty("globalMinIntervalMilliseconds", out var g) ? g.GetInt32() : 40,
            root.TryGetProperty("chatMinIntervalMilliseconds", out var c) ? c.GetInt32() : 1100,
            root.TryGetProperty("sendTimeoutSeconds", out var st) ? st.GetInt32() : 30);
    }
}

/// <summary>
/// Telegram 通道插件入口（旧 IHelperPlugin 契约 + 新扩展协商；C02 语义——缺扩展初始化前拒绝）。
/// 实例创建经 <see cref="TelegramChannelFactory"/>（宿主注册后按用户配置驱动）。
/// </summary>
[PluginEntry("im-telegram")]
public sealed class TelegramPlugin : IHelperPlugin
{
    public string Id => "im-telegram";
    public string DisplayName => "Telegram";
    public string Version => "1.0.0";

    internal IExtensionContext? Context { get; private set; }

    public async Task InitializeAsync(IPluginHostContext host, CancellationToken cancellationToken = default)
    {
        var extension = host as IPluginHostExtension
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ContractVersionMissing}] 宿主缺少 IM 扩展契约，插件拒绝以降级模式启动。");
        var context = await extension.NegotiateAsync(
            new ExtensionRequest(Id, HostingContract.Version, ["state-store", "credentials", "network", "blob"]),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ContractVersionMismatch}] 宿主契约版本不满足 {HostingContract.Version}，初始化拒绝。");
        Context = context;
    }

    public Task ShutdownAsync()
    {
        Context = null;
        return Task.CompletedTask;
    }
}

/// <summary>通道能力工厂：插件注册形态（Plugin → CapabilityFactory → EndpointInstance）。</summary>
public sealed class TelegramChannelFactory : ICapabilityFactory
{
    public string PluginId => "im-telegram";
    public string CapabilityId => "telegram-channel";
    public string MinContractVersion => HostingContract.Version;
    public IReadOnlyList<string> RequiredServices { get; } = ["state-store", "credentials", "network", "blob"];

    /// <summary>clock：测试注入可控时钟（T04 假时间推进）；生产为系统时钟。</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public Task<ICapabilityInstance> CreateAsync(InstanceCreationContext context, CancellationToken cancellationToken)
    {
        var configJson = "{}";   // 宿主在 StartAsync 的 ImChannelStartContext.ConfigJson 传入
        var instance = new TelegramChannel(
            context.Instance, context.Activation, context.Services, configJson, Clock);
        return Task.FromResult<ICapabilityInstance>(instance);
    }
}

/// <summary>通道实例：同时是 ICapabilityInstance 与 IImChannel（预览契约的组合形态；组合根按 IImChannel 绑定 Sink）。</summary>
public sealed class TelegramChannel : IImChannel, ICapabilityInstance
{
    private readonly IExtensionContext _services;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DateTimeOffset> _lastChatSend = [];
    private DateTimeOffset _lastAnySend = DateTimeOffset.MinValue;
    private Task? _pollTask;
    private TelegramChannelConfig _config = new("http://127.0.0.1:1", "unset");   // StartAsync 时被配置覆盖
    private TimeProvider Clock { get; }
    private long _botId;
    private long _offset;
    private volatile ImChannelRunState _state = ImChannelRunState.Stopped;
    private string? _stateReason;

    public TelegramChannel(InstanceRef instance, ActivationId activation, IExtensionContext services,
        string configJson, TimeProvider? clock = null)
    {
        Instance = instance;
        Activation = activation;
        _services = services;
        Clock = clock ?? TimeProvider.System;
        try
        {
            _config = TelegramChannelConfig.Parse(configJson);
        }
        catch (Exception)
        {
            // 工厂以空配置构造实例；StartAsync 传入正式配置时权威解析（缺配置在 Start 明确失败）
        }
    }

    public InstanceRef Instance { get; }
    public ActivationId Activation { get; }
    public string ChannelId => "telegram";
    public string DisplayName => "Telegram";
    public bool IsRunning => _state is ImChannelRunState.Running or ImChannelRunState.Degraded;

    public ImChannelCapabilities Capabilities { get; } = new(
        MaxTextChars: 4096, MaxOutFileBytes: 45L * 1024 * 1024, MaxInFileBytes: 20L * 1024 * 1024,
        MaxFilesPerMessage: 10, SupportsCaption: true, CaptionMaxChars: 1024,
        MarkdownMode: "None", ReplyWindow: TimeSpan.FromMinutes(2), GroupRatePerMinute: 18,
        HasOutboundIdempotencyKey: false);   // Telegram 无出站幂等键 → Unknown 不自动重发（T05）

    public event Action<ImChannelStatus>? StatusChanged;
    internal IImInboundSink? Sink { get; private set; }

    private void SetState(ImChannelRunState state, string? reason = null)
    {
        _state = state;
        _stateReason = reason;
        StatusChanged?.Invoke(new ImChannelStatus(state, reason, Clock.GetUtcNow()));
    }

    // ── 生命周期（§2.2：StartAsync 返回即具备接收能力；StopAsync 返回即停止完成）──

    public async Task StartAsync(ImChannelStartContext context, CancellationToken cancellationToken)
    {
        _config = TelegramChannelConfig.Parse(context.ConfigJson);
        Sink = context.Sink;
        SetState(ImChannelRunState.Starting);

        // getMe 探活与身份（T01）：AccountId = bot id，稳定
        var me = await CallApiAsync("getMe", null, cancellationToken).ConfigureAwait(false);
        if (me.StatusCode != 200)
            throw new InvalidOperationException($"getMe 失败（HTTP {me.StatusCode}），通道未启动。");
        using (var doc = JsonDocument.Parse(me.Body))
        {
            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok)
                throw new InvalidOperationException("getMe 返回 ok=false，通道未启动。");
            _botId = doc.RootElement.GetProperty("result").GetProperty("id").GetInt64();
        }

        // 游标恢复（协议态归插件命名空间存储 §8.1/§15.1）
        var stateStore = _services.GetService<IPluginStateStore>();
        var saved = stateStore is null ? null : await stateStore.GetAsync("offset", cancellationToken).ConfigureAwait(false);
        if (saved is not null && long.TryParse(saved, out var restored)) _offset = restored;

        SetState(ImChannelRunState.Running);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public async Task StopAsync(TimeSpan gracePeriod)
    {
        _cts.Cancel();
        if (_pollTask is not null)
        {
            var finished = await Task.WhenAny(_pollTask, Task.Delay(gracePeriod)).ConfigureAwait(false);
            if (finished != _pollTask)
            {
                SetState(ImChannelRunState.Failed, "停止超时：长轮询未在宽限期内退出（T06：明确失败而非假成功）");
                return;
            }
        }
        SetState(ImChannelRunState.Stopped);
    }

    public ValueTask DisposeAsync()
    {
        // 宿主停用路径：取消轮询并置 Stopped（IsRunning 立即为 false）；CTS 由 StopAsync 宽限后释放
        _cts.Cancel();
        SetState(ImChannelRunState.Stopped);
        return ValueTask.CompletedTask;
    }

    // ── 接收：单循环 + 连续已决前缀确认（channel-telegram.md §2.1/§2.2；T02/T03）──

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var body = $"offset={_offset}&timeout={_config.PollTimeoutSeconds}&limit={_config.PollLimit}&allowed_updates=%5B%22message%22%5D";
                var response = await CallApiAsync($"getUpdates?{body}", null, ct).ConfigureAwait(false);

                if (response.StatusCode != 200)
                {
                    // 非 200：先识别 409 两类冲突（body 携带 ok:false + description），其余按通用 HTTP 错误退避
                    var conflict = response.StatusCode == 409 ? ClassifyGetUpdatesConflict(response.Body) : null;
                    if (conflict == "webhook")
                    {
                        SetState(ImChannelRunState.Degraded, "409：webhook 激活冲突，需本机确认迁移（未自动清除 webhook）");
                    }
                    else if (conflict == "other-poller")
                    {
                        SetState(ImChannelRunState.Failed, "409：检测到其他 getUpdates 实例，本实例停止轮询（不争抢）");
                        return;
                    }
                    else
                    {
                        SetState(ImChannelRunState.Degraded, $"getUpdates HTTP {response.StatusCode}");
                    }
                    backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                using var doc = JsonDocument.Parse(response.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var okEl) && okEl.ValueKind != JsonValueKind.True)
                {
                    var description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    if (description.Contains("webhook is active", StringComparison.OrdinalIgnoreCase))
                    {
                        // T06：webhook 冲突——提示本机确认迁移，不静默 deleteWebhook
                        SetState(ImChannelRunState.Degraded, "409：webhook 激活冲突，需本机确认迁移（未自动清除 webhook）");
                        backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (description.Contains("terminated by other getUpdates", StringComparison.OrdinalIgnoreCase))
                    {
                        // T06：另一轮询实例——报告并停止本实例，不争抢、不清 webhook
                        SetState(ImChannelRunState.Failed, "409：检测到其他 getUpdates 实例，本实例停止轮询（不争抢）");
                        return;
                    }
                    SetState(ImChannelRunState.Degraded, $"getUpdates 失败：{description}");
                    backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                backoff = TimeSpan.FromSeconds(1);   // 成功重置退避
                SetState(ImChannelRunState.Running);

                var updates = root.GetProperty("result");
                if (updates.GetArrayLength() == 0)
                {
                    // 空批节奏（真实环境由服务端长轮询挂起；本地模拟/降级场景防止紧转）
                    await DelaySafeAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                    continue;
                }

                var advancePrefix = true;
                foreach (var update in updates.EnumerateArray())
                {
                    if (ct.IsCancellationRequested) return;
                    var updateId = update.GetProperty("update_id").GetInt64();
                    var message = Normalize(update);
                    if (message is null)
                    {
                        // sticker 等不处理：明确忽略，计入已确认（T01）
                        await AdvanceOffsetAsync(updateId).ConfigureAwait(false);
                        continue;
                    }
                    var accept = await Sink!.AcceptInboundAsync(message, ct).ConfigureAwait(false);
                    if (accept.IsDecided)
                    {
                        // Accepted/Duplicate/Rejected 均已决 → 推进并持久化游标（T03：落盘后才推进）
                        await AdvanceOffsetAsync(updateId).ConfigureAwait(false);
                    }
                    else
                    {
                        // Busy：立即停止本批后续确认（T02），背压暂停
                        SetState(ImChannelRunState.Degraded, "Inbox Busy：暂停拉取（不推进游标，平台保留未确认更新）");
                        advancePrefix = false;
                        await DelaySafeAsync(TimeSpan.FromMilliseconds(_config.BusyRetryMilliseconds), ct).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 网络异常退避 1s→2s→5s→15s（§2.2）
                SetState(ImChannelRunState.Degraded, $"轮询异常：{ex.Message}");
                try
                {
                    backoff = await BackoffAsync(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>409 冲突分类（T06）："webhook"（需本机迁移）｜"other-poller"（停止本实例）｜null（非冲突）。</summary>
    internal static string? ClassifyGetUpdatesConflict(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("description", out var d))
                return null;
            var description = d.GetString() ?? "";
            if (description.Contains("webhook is active", StringComparison.OrdinalIgnoreCase)) return "webhook";
            if (description.Contains("terminated by other getUpdates", StringComparison.OrdinalIgnoreCase)) return "other-poller";
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task AdvanceOffsetAsync(long updateId)
    {
        _offset = updateId + 1;
        var stateStore = _services.GetService<IPluginStateStore>();
        if (stateStore is not null)
            await stateStore.SetAsync("offset", _offset.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<TimeSpan> BackoffAsync(TimeSpan backoff, CancellationToken ct)
    {
        await DelaySafeAsync(backoff, ct).ConfigureAwait(false);
        return backoff >= TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : backoff * 2;
    }

    /// <summary>协议节奏（空批节奏/背压/退避）用真实时间——可控时钟只用于 DelayFor 限额计算（T04）。</summary>
    private async Task DelaySafeAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    // ── 出站（§3：sendMessage/429/无幂等键 Unknown；令牌桶 T04）──

    public async Task<DeliveryResult> SubmitDeliveryAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        // 发送前核对目标账号代次（§11.6 换号保护由 Outbox 兜底，此处核对账号一致性）
        var wait = DelayFor(request.ChatId, Clock.GetUtcNow());
        if (wait > TimeSpan.Zero)
            await DelaySafeAsync(wait, cancellationToken).ConfigureAwait(false);
        var sendAt = Clock.GetUtcNow();
        lock (_gate)
        {
            _lastAnySend = sendAt;
            _lastChatSend[request.ChatId] = sendAt;
        }

        try
        {
            var sendTimeout = TimeSpan.FromSeconds(_config.SendTimeoutSeconds);
            HttpResult? response = null;
            if (request.Files is { Count: > 0 })
            {
                // 文件：photo → sendPhoto，其余 → sendDocument（multipart；T07）
                var blobs = _services.GetService<IBlobService>();
                if (blobs is null)
                    return DeliveryResult.Rejected(ImErrorCodes.ServiceNotGranted, "Blob 服务未授予，文件发送不可用。");
                foreach (var file in request.Files)
                {
                    var field = file.MediaType is not null && file.MediaType.StartsWith("image/", StringComparison.Ordinal) ? "photo" : "document";
                    await using var content = await blobs.OpenReadAsync(file, "outbound-delivery", cancellationToken).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                    var multipart = BuildMultipart(
                        ("chat_id", request.ChatId),
                        (field, file.FileName, file.MediaType ?? "application/octet-stream", ms.ToArray()));
                    response = await CallApiAsync(field == "photo" ? "sendPhoto" : "sendDocument", multipart,
                        "multipart/form-data; boundary=" + MultipartBoundary, cancellationToken, sendTimeout).ConfigureAwait(false);
                }
            }
            else
            {
                var sendJson = $$"""{"chat_id":{{JsonSerializer.Serialize(request.ChatId)}},"text":{{JsonSerializer.Serialize(request.Text ?? "")}},"disable_web_page_preview":true}""";
                response = await CallApiAsync("sendMessage",
                    Encoding.UTF8.GetBytes(sendJson), "application/json", cancellationToken, sendTimeout).ConfigureAwait(false);
            }

            return InterpretSendResponse(response!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 超时/断连：结果未知（T05）——无幂等键，Outbox 不自动重发
            return DeliveryResult.Unknown($"telegram send unknown: {ex.Message}");
        }
    }

    internal static DeliveryResult InterpretSendResponse(HttpResult response)
    {
        // StatusCode 0 = 宿主网络层"取消/超时/断连"（不抛异常的约定）：平台是否收到未知 → Unknown
        if (response.StatusCode == 0)
            return DeliveryResult.Unknown("传输失败/超时：结果未知（平台是否收到未知）");
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            if (response.StatusCode == 429)
            {
                var retryAfter = 0;
                if (root.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r))
                    retryAfter = r.GetInt32();
                return DeliveryResult.Rejected(ImErrorCodes.Temporary, $"429 rate limited") with { RetryAfter = TimeSpan.FromSeconds(retryAfter) };
            }
            if (response.StatusCode == 200 && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var messageId = root.GetProperty("result").TryGetProperty("message_id", out var m)
                    ? m.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                return DeliveryResult.PlatformAccepted(messageId);
            }
            if (response.StatusCode is 400 or 403 or 404)
                return DeliveryResult.Rejected(ImErrorCodes.Permanent, Describe(root, response.StatusCode));
            return DeliveryResult.Rejected(ImErrorCodes.Temporary, Describe(root, response.StatusCode));
        }
        catch (JsonException)
        {
            return response.IsSuccess
                ? DeliveryResult.Unknown("响应不可解析（结果未知）")
                : DeliveryResult.Rejected(ImErrorCodes.Temporary, $"HTTP {response.StatusCode}");
        }
    }

    private static string Describe(JsonElement root, int status) =>
        root.TryGetProperty("description", out var d) ? $"HTTP {status}: {d.GetString()}" : $"HTTP {status}";

    /// <summary>令牌桶（T04）：全局 ≥GlobalMinInterval，单聊 ≥ChatMinInterval；返回需等待时长（0 = 立即）。</summary>
    internal TimeSpan DelayFor(string chatId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var globalWait = _lastAnySend == DateTimeOffset.MinValue
                ? TimeSpan.Zero
                : _lastAnySend + TimeSpan.FromMilliseconds(_config.GlobalMinIntervalMilliseconds) - now;
            var chatWait = !_lastChatSend.TryGetValue(chatId, out var lastChat)
                ? TimeSpan.Zero
                : lastChat + TimeSpan.FromMilliseconds(_config.ChatMinIntervalMilliseconds) - now;
            var wait = globalWait > chatWait ? globalWait : chatWait;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return wait;
        }
    }

    // ── 归一化（T01；channel-telegram.md §2.1 映射表）──

    internal ImInboundMessage? Normalize(JsonElement update)
    {
        if (!update.TryGetProperty("message", out var msg)) return null;
        var chat = msg.GetProperty("chat");
        var chatType = chat.TryGetProperty("type", out var t) ? t.GetString() : null;
        var normalizedType = chatType switch
        {
            "private" => ImChatType.Private,
            "group" or "supergroup" => ImChatType.Group,
            "channel" => ImChatType.Channel,
            _ => ImChatType.Unknown,
        };

        var from = msg.GetProperty("from");
        var senderId = from.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var isBot = from.TryGetProperty("is_bot", out var b) && b.ValueKind == JsonValueKind.True;
        var senderName = from.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;

        var text = "";
        if (msg.TryGetProperty("text", out var textEl)) text = textEl.GetString() ?? "";
        else if (msg.TryGetProperty("caption", out var capEl)) text = capEl.GetString() ?? "";

        List<ImAttachmentDescriptor>? attachments = null;
        if (msg.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Array)
        {
            JsonElement? largest = null;
            foreach (var size in photo.EnumerateArray())
            {
                if (largest is null) largest = size;
                else
                {
                    long cur = largest.Value.TryGetProperty("file_size", out var cs) ? cs.GetInt64() : largest.Value.GetProperty("width").GetInt64();
                    long next = size.TryGetProperty("file_size", out var ns) ? ns.GetInt64() : size.GetProperty("width").GetInt64();
                    if (next >= cur) largest = size;
                }
            }
            if (largest is { } chosen)
            {
                attachments ??= [];
                attachments.Add(new ImAttachmentDescriptor("telegram-file", 1,
                    chosen.GetProperty("file_id").GetString()!, null, "image/jpeg", ImAttachmentKind.Image,
                    chosen.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : null, null,
                    ImAttachmentState.Referenced, null));
            }
        }
        if (msg.TryGetProperty("document", out var document))
        {
            attachments ??= [];
            attachments.Add(new ImAttachmentDescriptor("telegram-file", 1,
                document.GetProperty("file_id").GetString()!,
                document.TryGetProperty("file_name", out var dn) ? dn.GetString() : null,
                document.TryGetProperty("mime_type", out var dm) ? dm.GetString() : null,
                ImAttachmentKind.File,
                document.TryGetProperty("file_size", out var dfs) ? dfs.GetInt64() : null, null,
                ImAttachmentState.Referenced, null));
        }
        if (msg.TryGetProperty("voice", out var voice))
        {
            attachments ??= [];
            attachments.Add(new ImAttachmentDescriptor("telegram-file", 1,
                voice.GetProperty("file_id").GetString()!, null,
                voice.TryGetProperty("mime_type", out var vm) ? vm.GetString() : null,
                ImAttachmentKind.Voice,
                voice.TryGetProperty("file_size", out var vfs) ? vfs.GetInt64() : null, null,
                ImAttachmentState.Referenced, null));
        }

        // B6（doc/Voice/01 缺陷#B2）：音乐/音频文件不再静默丢弃（此前连文本都没有 → 消息整体被忽略）——
        // 投递占位提示文本（无附件、不转写：音频文件本身不在语音识别范围）
        if (msg.TryGetProperty("audio", out var audioMsg))
        {
            var audioName = audioMsg.TryGetProperty("title", out var audioTitle) && !string.IsNullOrWhiteSpace(audioTitle.GetString())
                ? audioTitle.GetString()!
                : audioMsg.TryGetProperty("file_name", out var audioFileName) ? audioFileName.GetString() : null;
            var note = $"[音乐文件：{audioName ?? "未命名"}]（我暂不支持直接处理音频文件内容）";
            text = string.IsNullOrEmpty(text) ? note : $"{text} {note}";
        }

        // 纯 sticker/表情等：文本与附件皆空 → 明确忽略（不算丢消息，Accepted 计入已确认，T01）
        if (text.Length == 0 && (attachments is null || attachments.Count == 0)) return null;

        return new ImInboundMessage(
            ChannelId, Instance.InstanceId, _botId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            chat.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            normalizedType, senderId, senderName,
            IsSelf: senderId == _botId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IsBot: isBot,
            text,
            msg.GetProperty("message_id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            msg.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(dateEl.GetInt64())
                : Clock.GetUtcNow(),
            attachments);
    }

    // ── 附件物化（T07：getFile 重取下载路径 → Blob 服务 → ContentRef）──

    public async Task<ContentRef?> MaterializeAsync(ImAttachmentDescriptor descriptor, CancellationToken ct)
    {
        var blobs = _services.GetService<IBlobService>();
        if (blobs is null) return null;

        // file_id 长期稳定；下载路径经 getFile 重新获取（§8.3：临时路径失效 ≠ 附件过期）
        var getFileJson = $$"""{"file_id":{{JsonSerializer.Serialize(descriptor.RemoteId)}}}""";
        var response = await CallApiAsync("getFile",
            Encoding.UTF8.GetBytes(getFileJson), "application/json", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        if (response.StatusCode != 200 || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("file_path", out var pathEl))
        {
            return null;   // >20MB 或平台拒绝：路径不返回 → 附件明确拒绝（T07；调用方标记 Rejected 并说明）
        }
        var filePath = pathEl.GetString()!;

        var download = await CallTemplateAsync($"{_config.ApiBase}/file/bot{{token}}/{Uri.EscapeDataString(filePath)}", null, null, ct).ConfigureAwait(false);
        if (download.StatusCode != 200 || download.Body.Length > Capabilities.MaxInFileBytes) return null;

        await using var ms = new MemoryStream(download.Body);
        return await blobs.WriteAsync("inbound-attachment",
            ms, Capabilities.MaxInFileBytes,
            descriptor.Name ?? $"{descriptor.RemoteId}.bin", descriptor.MediaType, ct).ConfigureAwait(false);
    }

    // ── HTTP（T08：token 在 URL 路径；凭据服务注入 + 日志隐藏）──

    private const string MultipartBoundary = "ImTelegramBoundary7cd32e";

    private static byte[] BuildMultipart(params (string Name, string Value)[] fields)
    {
        using var ms = new MemoryStream();
        foreach (var (name, value) in fields)
        {
            ms.Write(Encoding.ASCII.GetBytes($"--{MultipartBoundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n"));
        }
        ms.Write(Encoding.ASCII.GetBytes($"--{MultipartBoundary}--\r\n"));
        return ms.ToArray();
    }

    private static byte[] BuildMultipart((string Name, string Value) field,
        (string FieldName, string FileName, string MediaType, byte[] Content) file)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(
            $"--{MultipartBoundary}\r\nContent-Disposition: form-data; name=\"{field.Name}\"\r\n\r\n{field.Value}\r\n"));
        ms.Write(Encoding.ASCII.GetBytes(
            $"--{MultipartBoundary}\r\nContent-Disposition: form-data; name=\"{file.FieldName}\"; filename=\"{file.FileName}\"\r\nContent-Type: {file.MediaType}\r\n\r\n"));
        ms.Write(file.Content);
        ms.Write(Encoding.ASCII.GetBytes("\r\n"));
        ms.Write(Encoding.ASCII.GetBytes($"--{MultipartBoundary}--\r\n"));
        return ms.ToArray();
    }

    private async Task<HttpResult> CallApiAsync(string methodWithQuery, byte[]? body, string? contentType, CancellationToken ct, TimeSpan? timeout = null) =>
        await CallTemplateAsync($"{_config.ApiBase}/bot{{token}}/{methodWithQuery}", body, contentType, ct, timeout).ConfigureAwait(false);

    /// <summary>文件下载走 {apiBase}/file/bot{token}/&lt;path&gt;（无 /method 段；§4getFile）。</summary>
    private Task<HttpResult> CallApiAsync(string methodWithQuery, string? jsonBody, CancellationToken ct) =>
        CallApiAsync(methodWithQuery, jsonBody is null ? null : Encoding.UTF8.GetBytes(jsonBody), null, ct);

    private async Task<HttpResult> CallTemplateAsync(string urlTemplate, byte[]? body, string? contentType, CancellationToken ct, TimeSpan? timeout = null)
    {
        var network = _services.GetService<INetworkService>()
            ?? throw new InvalidOperationException("网络服务未授予，通道无法通信。");
        var credentials = _services.GetService<ICredentialService>()
            ?? throw new InvalidOperationException("凭据服务未授予。");
        var injection = credentials.InjectionFor(_config.SecretRef, SecretPlacement.UrlPathSegment);
        return await network.SendWithUrlPathTokenAsync(
            urlTemplate,
            body is null ? "GET" : "POST",
            body, contentType, injection, null,
            timeout ?? TimeSpan.FromSeconds(_config.PollTimeoutSeconds + 15), ct).ConfigureAwait(false);
    }
}
