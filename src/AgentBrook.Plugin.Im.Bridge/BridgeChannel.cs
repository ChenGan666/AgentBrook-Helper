using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Im.Bridge;

/// <summary>桥通道配置（桥契约 v1.5 §1）。</summary>
public sealed record BridgeChannelConfig(
    string ChannelId,
    string DisplayName,
    string BridgeUrl,
    string SecretRef,
    int SendTimeoutSeconds = 15,
    int AttachmentRetentionHours = 24)
{
    public static BridgeChannelConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var channelId = root.TryGetProperty("channelId", out var c) ? c.GetString() : null;
        var bridgeUrl = root.TryGetProperty("bridgeUrl", out var b) ? b.GetString() : null;
        var secretRef = root.TryGetProperty("secretRef", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(channelId)) throw new InvalidOperationException("[BadPayload] 缺少 channelId。");
        if (string.IsNullOrWhiteSpace(bridgeUrl) || !Uri.TryCreate(bridgeUrl, UriKind.Absolute, out var uri)
            || !IPAddressIsLoopback(uri.Host))
            throw new InvalidOperationException($"[BadPayload] bridgeUrl 必须是回环地址（P0 仅同机）：{bridgeUrl}");
        if (string.IsNullOrWhiteSpace(secretRef)) throw new InvalidOperationException("[BadPayload] 缺少 secretRef。");
        return new BridgeChannelConfig(
            channelId,
            root.TryGetProperty("displayName", out var d) ? d.GetString() ?? channelId : channelId,
            bridgeUrl.TrimEnd('/'), secretRef,
            root.TryGetProperty("sendTimeoutSeconds", out var t) ? t.GetInt32() : 15,
            root.TryGetProperty("attachmentRetentionHours", out var r) ? r.GetInt32() : 24);
    }

    private static bool IPAddressIsLoopback(string host) =>
        host is "127.0.0.1" or "localhost" or "::1";
}


/// <summary>桥通道能力：出站幂等键=true（桥持久幂等 → Unknown 可安全自动重试）；文本/文件上限按桥契约。</summary>
public sealed class BridgeChannelFactory : ICapabilityFactory
{
    public string PluginId => "im-bridge";
    public string CapabilityId => "bridge-channel";
    public string MinContractVersion => HostingContract.Version;
    public IReadOnlyList<string> RequiredServices { get; } = ["state-store", "credentials", "network", "webhook", "blob"];

    public Task<ICapabilityInstance> CreateAsync(InstanceCreationContext context, CancellationToken cancellationToken)
    {
        var instance = new BridgeChannel(context.Instance, context.Activation, context.Services, "{}");
        return Task.FromResult<ICapabilityInstance>(instance);
    }
}

/// <summary>
/// 同机桥通道（IImChannel + 查询/取消可选能力 + ICapabilityInstance）。
/// /send 幂等键 = DeliveryId（稳定，重试不制造新副作用）；附件经宿主签发的临时下载 URL 供桥拉取
/// （文件 ID 稳定、凭证每次重签、10 分钟有效、可撤销——契约 v1.3）。
/// </summary>
public sealed class BridgeChannel : IImChannel, IDeliveryStatusProvider, IDeliveryCancellationProvider, ICapabilityInstance
{
    private readonly IExtensionContext _services;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (ContentRef File, string TempToken, DateTimeOffset Expires)> _fileDownloads = [];
    private volatile ImChannelRunState _state = ImChannelRunState.Stopped;
    private string? _stateReason;
    private IRegistrationLease? _inboundLease;
    private IRegistrationLease? _fileLease;

    public BridgeChannel(InstanceRef instance, ActivationId activation, IExtensionContext services,
        string configJson, TimeProvider? clock = null)
    {
        Instance = instance;
        Activation = activation;
        _services = services;
        Clock = clock ?? TimeProvider.System;
        try { Config = BridgeChannelConfig.Parse(configJson); }
        catch (Exception) { /* 工厂以空配置构造实例；StartAsync 权威解析 */ }
    }

    public InstanceRef Instance { get; }
    public ActivationId Activation { get; }
    private TimeProvider Clock { get; }
    private BridgeChannelConfig Config { get; set; } = new("bridge-x", "桥", "http://127.0.0.1:1", "unset");
    private IWebhookService? Webhook { get; set; }

    public string ChannelId => Config.ChannelId;
    public string DisplayName => Config.DisplayName;
    public bool IsRunning => _state is ImChannelRunState.Running or ImChannelRunState.Degraded;

    public ImChannelCapabilities Capabilities { get; } = new(
        MaxTextChars: 4096, MaxOutFileBytes: 50L * 1024 * 1024, MaxInFileBytes: 50L * 1024 * 1024,
        MaxFilesPerMessage: 10, SupportsCaption: false, CaptionMaxChars: 0,
        MarkdownMode: "None", ReplyWindow: null, GroupRatePerMinute: 0,
        HasOutboundIdempotencyKey: true);   // 桥持久幂等（v1.2）→ Unknown 可安全自动重试

    /// <summary>桥上报的登录账号（/health；账号更换检测属宿主/Messaging 语义）。</summary>
    public string? BridgeAccountId { get; private set; }

    public event Action<ImChannelStatus>? StatusChanged;
    internal IImInboundSink? Sink { get; private set; }

    private void SetState(ImChannelRunState state, string? reason = null)
    {
        _state = state;
        _stateReason = reason;
        StatusChanged?.Invoke(new ImChannelStatus(state, reason, Clock.GetUtcNow()));
    }

    // ── 生命周期 ──

    public async Task StartAsync(ImChannelStartContext context, CancellationToken cancellationToken)
    {
        Config = BridgeChannelConfig.Parse(context.ConfigJson);
        Sink = context.Sink;
        Webhook = _services.GetService<IWebhookService>()
            ?? throw new InvalidOperationException("[ServiceNotGranted] Webhook 服务未授予，桥入站不可用。");
        SetState(ImChannelRunState.Starting);

        // 入站路由：POST /im/bridge/{channelId}/inbound（桥推入；凭据验签在处理器内做）
        _inboundLease?.Dispose();
        _inboundLease = Webhook.RegisterRoute(
            $"/im/bridge/{Config.ChannelId}/inbound",
            HandleInboundAsync,
            new WebhookLimits(MaxBodyBytes: 16L * 1024 * 1024, Timeout: TimeSpan.FromSeconds(30)));

        // 出站附件下载路由（前缀通配）：GET /im/bridge/{channelId}/file/{id}?token=（独立下载 token，非通信 token）
        _fileLease?.Dispose();
        _fileLease = Webhook.RegisterRoute(
            $"/im/bridge/{Config.ChannelId}/file/*",
            HandleFileDownloadAsync,
            new WebhookLimits(MaxBodyBytes: 4096, Timeout: TimeSpan.FromMinutes(10)));

        // /health 探活 + 账号上报（账号更换即隔离由宿主/Messaging 执行）
        var health = await CallBridgeAsync("GET", "/health", null, cancellationToken).ConfigureAwait(false);
        if (health.StatusCode != 200)
            throw new InvalidOperationException($"[Temporary] 桥 /health 不可达（HTTP {health.StatusCode}），通道未启动。");
        using (var doc = JsonDocument.Parse(health.Body))
        {
            BridgeAccountId = doc.RootElement.TryGetProperty("accountId", out var a) ? a.GetString() : null;
        }

        SetState(ImChannelRunState.Running);
    }

    public async Task StopAsync(TimeSpan gracePeriod)
    {
        _inboundLease?.Dispose();   // 租约撤销：旧回调失效（§15.2 可撤销租约）
        _fileLease?.Dispose();
        SetState(ImChannelRunState.Stopped);
        await Task.Yield();
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        SetState(ImChannelRunState.Stopped);
        _inboundLease?.Dispose();
        _fileLease?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 入站（桥推入 → 验签 → 归一化 → Sink；B01）──

    internal async Task<WebhookResponse> HandleInboundAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var credentials = _services.GetService<ICredentialService>();
        var tokenHeader = request.Headers.FirstOrDefault(h => h.Name.Equals("X-Im-Bridge-Token", StringComparison.OrdinalIgnoreCase)).Value;
        if (credentials is null || !credentials.Verify(Config.SecretRef, tokenHeader ?? ""))
            return new WebhookResponse(401, Encoding.UTF8.GetBytes("{\"code\":1,\"message\":\"bad token\"}"));

        ImInboundMessage? message;
        try
        {
            message = NormalizeInbound(request.Body);
        }
        catch (JsonException)
        {
            return new WebhookResponse(400, Encoding.UTF8.GetBytes("{\"code\":1,\"message\":\"bad json\"}"));
        }
        if (message is null)
            return new WebhookResponse(400, Encoding.UTF8.GetBytes("{\"code\":1,\"message\":\"missing required fields\"}"));

        if (Sink is null)
            return new WebhookResponse(503, Encoding.UTF8.GetBytes("{\"code\":1,\"message\":\"sink not ready\"}"));

        var accept = await Sink.AcceptInboundAsync(message, cancellationToken).ConfigureAwait(false);
        return accept.State switch
        {
            ImAcceptState.Accepted => new WebhookResponse(200, Encoding.UTF8.GetBytes("{\"code\":0,\"state\":\"accepted\"}")),
            ImAcceptState.Duplicate => new WebhookResponse(200, Encoding.UTF8.GetBytes("{\"code\":0,\"state\":\"duplicate\"}")),
            ImAcceptState.Rejected => new WebhookResponse(200, Encoding.UTF8.GetBytes(
                ImJson.Serialize(new { code = 0, state = "rejected", message = accept.ReasonCode }))),
            ImAcceptState.Busy => new WebhookResponse(429, Encoding.UTF8.GetBytes(
                ImJson.Serialize(new { code = 1, message = "busy", retryAfter = (int)(accept.RetryAfter?.TotalSeconds ?? 3) }))),
            _ => new WebhookResponse(500, Encoding.UTF8.GetBytes("{\"code\":1}")),
        };
    }

    /// <summary>桥契约 §2 入站 JSON → 归一化消息；附件 data/url → 描述符（url 仅 bridgeUrl 同源，B11）。</summary>
    internal ImInboundMessage? NormalizeInbound(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var e) ? e.GetString() : null;

        var accountId = Get("accountId");
        var chatId = Get("chatId");
        var senderId = Get("senderId");
        var messageId = Get("messageId");
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(chatId) ||
            string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(messageId))
            return null;

        var chatType = Get("chatType") switch
        {
            "single" => ImChatType.Private,
            "group" => ImChatType.Group,
            _ => ImChatType.Unknown,
        };

        List<ImAttachmentDescriptor>? attachments = null;
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            attachments = [];
            foreach (var f in files.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                var mediaType = f.TryGetProperty("mediaType", out var m) ? m.GetString() : null;
                var kind = f.TryGetProperty("kind", out var k) ? k.GetString() : "file";
                var descriptor = new ImAttachmentDescriptor(
                    Provider: "bridge-blob",
                    SchemaVersion: 1,
                    RemoteId: $"loc-{messageId}-{attachments.Count}",   // 桥本地稳定 locator（示例形态）
                    Name: name,
                    MediaType: mediaType,
                    Kind: kind switch
                    {
                        "image" => ImAttachmentKind.Image,
                        "voice" => ImAttachmentKind.Voice,
                        "video" => ImAttachmentKind.Video,
                        _ => ImAttachmentKind.File,
                    },
                    DeclaredBytes: f.TryGetProperty("declaredBytes", out var db) ? db.GetInt64() : null,
                    ExpiresAt: Clock.GetUtcNow() + TimeSpan.FromHours(Config.AttachmentRetentionHours),
                    State: ImAttachmentState.Referenced,
                    LocalBlobId: null);

                if (f.TryGetProperty("data", out var data))
                {
                    // data（Base64 ≤8MB）：字节已随请求到达——staging 落盘（accepted 承诺=描述符可恢复）
                    var bytes = Convert.FromBase64String(data.GetString() ?? "");
                    var blobs = _services.GetService<IBlobService>();
                    if (blobs is not null)
                    {
                        using var ms = new MemoryStream(bytes);
                        var contentRef = blobs.WriteAsync("inbound-staging", ms, bytes.Length, name ?? "file", mediaType, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                        descriptor = descriptor with { RemoteId = contentRef.BlobId, LocalBlobId = contentRef.BlobId };
                    }
                }
                else if (f.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString() ?? "";
                    // 仅 bridgeUrl 同源（B11/契约 §6）：非同源直接拒绝该附件
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                        !u.IsAbsoluteUri || u.Host != new Uri(Config.BridgeUrl).Host || u.Port != new Uri(Config.BridgeUrl).Port)
                        continue;   // 丢弃该附件（消息文本仍处理）
                    descriptor = descriptor with { RemoteId = url };
                }
                else
                {
                    continue;   // 无 data 也无 url：跳过该附件
                }
                attachments.Add(descriptor);
            }
            if (attachments.Count == 0) attachments = null;
        }

        var timestamp = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64())
            : Clock.GetUtcNow();

        return new ImInboundMessage(
            ChannelId, Instance.InstanceId, accountId, chatId, chatType,
            senderId, Get("senderName"),
            IsSelf: false,   // 桥推入的为对端消息；防回声由 IsSelf 语义在 Sink 侧另有判定
            IsBot: root.TryGetProperty("senderIsBot", out var b) && b.ValueKind == JsonValueKind.True,
            Get("text") ?? "",
            messageId, timestamp, attachments);
    }

    // ── 出站（/send + 附件下载 URL 签发；B02/B03）──

    public async Task<DeliveryResult> SubmitDeliveryAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        var sendTimeout = TimeSpan.FromSeconds(Config.SendTimeoutSeconds);
        var files = new List<object>();
        if (request.Files is { Count: > 0 })
        {
            var webhook = Webhook ?? throw new InvalidOperationException("[ServiceNotGranted] Webhook 服务未授予。");
            for (var i = 0; i < request.Files.Count; i++)
            {
                var file = request.Files[i];
                var fileId = $"bf-{request.DeliveryId}-{i}";           // 文件 ID 稳定（重试不变）
                var tempToken = NewDownloadToken();                    // 凭证每次重签（v1.3）
                var expires = Clock.GetUtcNow() + TimeSpan.FromMinutes(10);
                lock (_gate) _fileDownloads[fileId] = (file, tempToken, expires);
                var url = $"{Webhook.BaseUrl}/im/bridge/{Config.ChannelId}/file/{fileId}?token={Uri.EscapeDataString(tempToken)}";
                files.Add(new { name = file.FileName, mediaType = file.MediaType, sha256 = file.Sha256, length = file.Length, url });
            }
        }

        var payload = ImJson.Serialize(new
        {
            outboundMessageId = request.DeliveryId,                     // 幂等键 = DeliveryId（稳定）
            accountId = request.AccountId,
            accountEpoch = request.AccountEpoch,
            chatId = request.ChatId,
            text = request.Text,
            deadline = request.Deadline?.ToString("O"),
            files,
        });

        HttpResult response;
        try
        {
            response = await CallBridgeAsync("POST", "/send", Encoding.UTF8.GetBytes(payload), cancellationToken, sendTimeout)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // 服务停止：现场保留
        }
        catch (Exception ex)
        {
            // 传输失败（含客户端超时）= 结果未知；桥持久幂等 → Outbox 可安全自动重试同 ID（B04）
            return DeliveryResult.Unknown($"bridge send unknown: {ex.Message}");
        }

        return InterpretSendResponse(response);
    }

    internal static DeliveryResult InterpretSendResponse(HttpResult response)
    {
        // 网关级失败先于 body 解析：桥进程崩溃时 listener 合成 502/504、连接重置/空体——语义=结果未知（F15/F16）
        // StatusCode 0 = 宿主网络层"取消/超时/断连"（不抛异常的约定）→ 同样结果未知
        if (response.StatusCode == 0 || response.StatusCode is 502 or 503 or 504 || response.Body.Length == 0)
            return DeliveryResult.Unknown($"bridge gateway failure HTTP {response.StatusCode}");
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code != 0)
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                return response.StatusCode == 409
                    ? DeliveryResult.Rejected(ImErrorCodes.Permanent, $"幂等键冲突：{message}")
                    : DeliveryResult.Rejected(ImErrorCodes.Temporary, $"bridge error: {message}");
            }
            var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
            return state switch
            {
                "accepted" => DeliveryResult.Accepted(),                 // 已持久化受理（责任在桥）
                "sent" => DeliveryResult.PlatformAccepted(
                    root.TryGetProperty("platformMsgId", out var p) ? p.GetString() : null),
                "unknown" => DeliveryResult.Unknown(root.TryGetProperty("message", out var um) ? um.GetString() : "unknown"),
                "cancelled" => DeliveryResult.Rejected("Cancelled", "取消墓碑拦截（B05）"),   // Outbox → Cancelled
                _ => DeliveryResult.Unknown($"未知受理态：{state}"),
            };
        }
        catch (JsonException)
        {
            return response.IsSuccess
                ? DeliveryResult.Unknown("桥响应不可解析（结果未知）")
                : DeliveryResult.Rejected(ImErrorCodes.Temporary, $"HTTP {response.StatusCode}");
        }
    }

    // ── 查询/取消（公共可选能力实现——桥=true；B05/B07/B08/B09）──

    public async Task<DeliveryTrackingState> QueryAsync(string deliveryId, CancellationToken cancellationToken)
    {
        HttpResult response;
        try
        {
            response = await CallBridgeAsync("GET", $"/delivery-status?id={Uri.EscapeDataString(deliveryId)}", null,
                cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return DeliveryTrackingState.Unknown;   // 查询失败按退避重查（不回退/不重发）
        }
        if (response.StatusCode == 404) return DeliveryTrackingState.NotFound;
        if (response.StatusCode != 200) return DeliveryTrackingState.Unknown;
        using var doc = JsonDocument.Parse(response.Body);
        var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
        return state switch
        {
            "accepted" => DeliveryTrackingState.AcceptedByProvider,
            "sent" => DeliveryTrackingState.Sent,
            "unknown" => DeliveryTrackingState.Unknown,
            "failed" => DeliveryTrackingState.Failed,
            "cancelled" => DeliveryTrackingState.Cancelled,
            _ => DeliveryTrackingState.Unknown,
        };
    }

    public async Task<DeliveryCancelState> CancelAsync(string deliveryId, CancellationToken cancellationToken)
    {
        HttpResult response;
        try
        {
            var payload = ImJson.Serialize(new { id = deliveryId });
            response = await CallBridgeAsync("POST", "/delivery-cancel", Encoding.UTF8.GetBytes(payload),
                cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return DeliveryCancelState.Unknown;   // 受理方离线/响应丢失 → 取消待确认
        }
        if (response.StatusCode != 200) return DeliveryCancelState.Unknown;
        using var doc = JsonDocument.Parse(response.Body);
        var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
        return state switch
        {
            "Cancelled" => DeliveryCancelState.Cancelled,
            "AlreadyDispatched" => DeliveryCancelState.AlreadyDispatched,
            _ => DeliveryCancelState.Unknown,
        };
    }

    // ── 附件物化（入站 staging → 正式 Blob；B11）──

    public async Task<ContentRef?> MaterializeAsync(ImAttachmentDescriptor descriptor, CancellationToken ct)
    {
        var blobs = _services.GetService<IBlobService>();
        if (blobs is null) return null;
        // bridge-blob 描述符：RemoteId 即宿主 staging BlobId（data 路径）；url 路径的字节由桥托管（拉取需授权 URL——
        // 同机回环；此处经宿主网络服务按授权拉取）
        if (descriptor.RemoteId.StartsWith("blb-", StringComparison.Ordinal))
        {
            await using var staged = await blobs.OpenReadAsync(new ContentRef(descriptor.RemoteId, "", 0, "", null),
                "inbound-staging", ct).ConfigureAwait(false);
            // staging 引用缺 sha/length——桥数据路径以 staging 内容为准复制
            using var ms = new MemoryStream();
            await staged.CopyToAsync(ms, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();
            return await blobs.WriteAsync("inbound-attachment", new MemoryStream(bytes),
                Capabilities.MaxInFileBytes, descriptor.Name ?? "file", descriptor.MediaType, ct).ConfigureAwait(false);
        }
        // url 路径：桥托管的回环 URL（保留期内可取；过期降级）
        if (Uri.TryCreate(descriptor.RemoteId, UriKind.Absolute, out var u) && IsLoopbackHost(u.Host))
        {
            var network = _services.GetService<INetworkService>();
            if (network is null) return null;
            var result = await network.SendAsync(new HttpRequestSpec(HttpMethod.Get, descriptor.RemoteId,
                [], null, null, TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
            if (result.StatusCode != 200) return null;
            await using var ms = new MemoryStream(result.Body);
            return await blobs.WriteAsync("inbound-attachment", ms, Capabilities.MaxInFileBytes,
                descriptor.Name ?? "file", descriptor.MediaType, ct).ConfigureAwait(false);
        }
        return null;
    }

    // ── 文件下载服务（桥拉取出站附件；token 验证 + 10 分钟有效）──

    private static string NewDownloadToken() => "dt-" + Guid.NewGuid().ToString("N");

    private async Task<WebhookResponse> HandleFileDownloadAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var path = request.Path;
        var fileId = path[(path.LastIndexOf('/') + 1)..];
        // 下载 token 从查询串取（桥拉取 URL 形如 ...?token=xxx；独立于通信 token）
        var provided = ExtractQueryValue(request.Query, "token") ?? "";

        (ContentRef File, string TempToken, DateTimeOffset Expires) entry;
        lock (_gate)
        {
            if (!_fileDownloads.TryGetValue(fileId, out entry!))
                return new WebhookResponse(404, Encoding.UTF8.GetBytes("{}"));
        }
        if (entry.Expires < Clock.GetUtcNow())
            return new WebhookResponse(403, Encoding.UTF8.GetBytes("{\"message\":\"download token expired\"}"));
        if (provided != entry.TempToken)
            return new WebhookResponse(403, Encoding.UTF8.GetBytes("{\"message\":\"bad download token\"}"));

        var blobs = _services.GetService<IBlobService>();
        if (blobs is null) return new WebhookResponse(503, Encoding.UTF8.GetBytes("{}"));
        await using var stream = await blobs.OpenReadAsync(entry.File, "outbound-delivery", cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return new WebhookResponse(200, ms.ToArray());
    }

    private static string? ExtractQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var prefix = key + "=";
        var idx = query.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        var end = query.IndexOf('&', start);
        return Uri.UnescapeDataString(end < 0 ? query[start..] : query[start..end]);
    }

    // ── HTTP ──

    private async Task<HttpResult> CallBridgeAsync(string method, string pathAndQuery, byte[]? body,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var network = _services.GetService<INetworkService>()
            ?? throw new InvalidOperationException("[ServiceNotGranted] 网络服务未授予。");
        var credentials = _services.GetService<ICredentialService>()
            ?? throw new InvalidOperationException("[ServiceNotGranted] 凭据服务未授予。");
        var injection = credentials.InjectionFor(Config.SecretRef, SecretPlacement.HeaderValue);
        var headers = new List<HttpHeader>();
        // HeaderValue 注入由 NetworkService 应用于 X-Im-Bridge-Token——见 SendAsync 的 ApplyHeaderInjections
        return await network.SendAsync(new HttpRequestSpec(
            new HttpMethod(method),
            $"{Config.BridgeUrl}{pathAndQuery}",
            headers,
            body,
            [injection],
            timeout ?? TimeSpan.FromSeconds(Config.SendTimeoutSeconds)), ct).ConfigureAwait(false);
    }
    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1";
}

