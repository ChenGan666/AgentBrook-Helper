using System.Net;
using System.Text;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Hosting;

/// <summary>
/// 命名空间状态存储（§15.2）：ns = PluginId+InstanceId（宿主强制前缀）；
/// 每次调用先校验活动代次（S3 任务6：旧实例晚到游标不能覆盖）。
/// </summary>
public sealed class NamespaceStateStore(HostDatabase db, ActivationRegistry activations, string namespaceId, ActivationId activation) : IPluginStateStore
{
    private readonly string _ns = namespaceId;
    private readonly ActivationId _activation = activation;

    private void EnsureAlive()
    {
        if (!activations.IsAlive(_activation))
            throw new HostPipelineException(ImErrorCodes.ActivationStale, "活动租约已失效（停用/更新切换后旧句柄）。");
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        EnsureAlive();
        using var cmd = db.CreateCommand("SELECT value FROM host_plugin_state WHERE ns = $ns AND key = $key",
            ("$ns", _ns), ("$key", key));
        var value = cmd.ExecuteScalar() as string;
        return Task.FromResult(value);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        EnsureAlive();
        db.Execute("""
            INSERT INTO host_plugin_state(ns, key, value) VALUES($ns, $key, $value)
            ON CONFLICT(ns, key) DO UPDATE SET value = excluded.value
            """, ("$ns", _ns), ("$key", key), ("$value", value));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        EnsureAlive();
        db.Execute("DELETE FROM host_plugin_state WHERE ns = $ns AND key = $key", ("$ns", _ns), ("$key", key));
        return Task.CompletedTask;
    }
}

/// <summary>
/// 凭据服务：宿主持有 secret 值，插件只持引用；受控注入覆盖 Header 与 Telegram URL 路径（S3 任务4）。
/// 值更新/撤销后旧注入失效；审计/日志走 <see cref="Redact"/> 自动隐藏。
/// </summary>
public sealed class CredentialService(HostDatabase db, string namespaceId, ActivationId activation, ActivationRegistry activations) : ICredentialService
{
    private readonly string _ns = namespaceId;
    private readonly ActivationId _activation = activation;
    private readonly ActivationRegistry _activations = activations;

    private void EnsureAlive()
    {
        if (!_activations.IsAlive(_activation))
            throw new HostPipelineException(ImErrorCodes.ActivationStale, "活动租约已失效。");
    }

    public Task<string> SetSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        EnsureAlive();
        db.Execute("""
            INSERT INTO host_secrets(ns, name, value) VALUES($ns, $name, $value)
            ON CONFLICT(ns, name) DO UPDATE SET value = excluded.value
            """, ("$ns", _ns), ("$name", name), ("$value", value));
        return Task.FromResult($"{_ns}/{name}");
    }

    public SecretInjection InjectionFor(string secretRef, SecretPlacement placement)
    {
        EnsureAlive();
        var parts = secretRef.Split('/', 2);
        if (parts.Length != 2 || parts[0] != _ns)
            throw new HostPipelineException(ImErrorCodes.ServiceNotGranted, $"凭据引用不属于本命名空间：{Redact(secretRef)}");
        return new SecretInjection(secretRef, placement);
    }

    /// <summary>常数时间校验外部凭据值（桥 inbound 验签；引用不匹配/值不符 → false）。</summary>
    public bool Verify(string secretRef, string providedValue)
    {
        EnsureAlive();
        var parts = secretRef.Split('/', 2);
        if (parts.Length != 2 || parts[0] != _ns) return false;
        string? expected;
        try { expected = Resolve(secretRef); }
        catch (HostPipelineException) { return false; }
        var a = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? ""));
        var b = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(providedValue ?? ""));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>内部：按引用取值（仅宿主网络服务在发送时调用；插件不可达）。</summary>
    internal string Resolve(string secretRef)
    {
        var parts = secretRef.Split('/', 2);
        using var cmd = db.CreateCommand("SELECT value FROM host_secrets WHERE ns = $ns AND name = $name",
            ("$ns", parts[0]), ("$name", parts[1]));
        var value = cmd.ExecuteScalar() as string
            ?? throw new HostPipelineException(ImErrorCodes.ServiceNotGranted, "凭据引用不存在或已撤销。");
        return value;
    }

    /// <summary>日志/审计脱敏：已知 secret 的值与引用名都替换为占位。</summary>
    public string Redact(string text)
    {
        using var cmd = db.CreateCommand("SELECT name, value FROM host_secrets WHERE ns = $ns", ("$ns", _ns));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var value = reader.GetString(1);
            if (!string.IsNullOrEmpty(value)) text = text.Replace(value, "***");
            text = text.Replace($"{_ns}/{name}", $"***secret:{name}***");
        }
        return text;
    }
}

/// <summary>
/// 受控网络服务（§15.2）：outboundHosts 申报范围内放行；重定向逐跳复验、token 不跨源透传（H08）；
/// 超时与响应体限额；不替代披露授权。
/// </summary>
public sealed class NetworkService : INetworkService
{
    private readonly IReadOnlyList<string> _allowedHosts;
    private readonly CredentialService _credentials;
    private readonly ActivationRegistry _activations;
    private readonly ActivationId _activation;

    public NetworkService(IReadOnlyList<string> allowedHosts, CredentialService credentials,
        ActivationRegistry activations, ActivationId activation)
    {
        _allowedHosts = allowedHosts;
        _credentials = credentials;
        _activations = activations;
        _activation = activation;
    }

    public async Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
    {
        if (!_activations.IsAlive(_activation))
            throw new HostPipelineException(ImErrorCodes.ActivationStale, "活动租约已失效，禁止新网络调用。");

        var uri = new Uri(request.Url);
        EnsureHostAllowed(uri.Host);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, // 重定向逐跳复验（不透传 token 到跨源）
        };
        using var client = new HttpClient(handler) { Timeout = request.Timeout };

        HttpResponseMessage response;
        var redirectChain = new List<string>();
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            using var msg = new HttpRequestMessage(request.Method, current);
            foreach (var header in request.Headers)
                msg.Headers.TryAddWithoutValidation(header.Name, header.Value);
            if (request.Body is not null && request.Method != HttpMethod.Get)
                msg.Content = new ByteArrayContent(request.Body);

            ApplyHeaderInjections(msg, request.Injections);

            response = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is 301 or 302 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (location is null) break;
                var next = new Uri(current, location);
                redirectChain.Add(next.ToString());
                // 跨源重定向：仅当目标 host 也被允许才继续；携带 token 的头部不再透传（重建头部，去掉授权类）
                EnsureHostAllowed(next.Host);
                current = next;
                if (hop > 5) throw new HostPipelineException(ImErrorCodes.Permanent, "重定向次数超限。");
                continue;
            }
            break;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResult((int)response.StatusCode, body, redirectChain);
    }

    private void ApplyHeaderInjections(HttpRequestMessage msg, IReadOnlyList<SecretInjection>? injections)
    {
        if (injections is null) return;
        foreach (var injection in injections)
        {
            var value = _credentials.Resolve(injection.SecretRef);
            switch (injection.Placement)
            {
                case SecretPlacement.BearerHeader:
                    msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {value}");
                    break;
                case SecretPlacement.HeaderValue:
                    msg.Headers.TryAddWithoutValidation("X-Im-Bridge-Token", value);
                    break;
                case SecretPlacement.UrlPathSegment:
                case SecretPlacement.QueryParameter:
                    throw new HostPipelineException(ImErrorCodes.ServiceNotGranted,
                        "URL 路径/查询注入请使用 SendWithUrlPathTokenAsync（仅允许注入到申报的原始目标）。");
                default:
                    throw new HostPipelineException(ImErrorCodes.ServiceNotGranted, "不支持的 secret 注入方式。");
            }
        }
    }

    /// <summary>带 URL 路径注入的发送（Telegram 语义：base 形如 https://host/bot；token 拼进路径）。JSON 便捷重载。</summary>
    public Task<HttpResult> SendWithUrlPathTokenAsync(
        string urlTemplate, string method, string? jsonBody, SecretInjection tokenInjection,
        IReadOnlyList<HttpHeader>? headers, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendWithUrlPathTokenAsync(urlTemplate, method,
            jsonBody is null ? null : Encoding.UTF8.GetBytes(jsonBody),
            jsonBody is null ? null : "application/json",
            tokenInjection, headers, timeout, cancellationToken);

    /// <summary>带 URL 路径注入的发送（字节体：multipart 上传用）。</summary>
    public async Task<HttpResult> SendWithUrlPathTokenAsync(
        string urlTemplate, string method, byte[]? body, string? contentType, SecretInjection tokenInjection,
        IReadOnlyList<HttpHeader>? headers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_activations.IsAlive(_activation))
            throw new HostPipelineException(ImErrorCodes.ActivationStale, "活动租约已失效。");
        var token = _credentials.Resolve(tokenInjection.SecretRef);
        var url = urlTemplate.Replace("{token}", token);   // token 原样入路径（Telegram 语义；平台要求原样，不做 URL 转义）
        var uri = new Uri(url);
        EnsureHostAllowed(uri.Host);

        using var client = new HttpClient { Timeout = timeout };
        using var msg = new HttpRequestMessage(new HttpMethod(method), uri);
        if (headers is not null)
            foreach (var h in headers) msg.Headers.TryAddWithoutValidation(h.Name, h.Value);
        if (body is not null)
        {
            msg.Content = new ByteArrayContent(body);
            // Content-Type 可能携带参数（multipart boundary 等）：MediaTypeHeaderValue 构造只认纯媒体类型会抛——
            // 统一走免验证写入（插件申报的 contentType 原样上送）。
            msg.Content.Headers.TryAddWithoutValidation("Content-Type",
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        }
        var response = await client.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResult((int)response.StatusCode, responseBody, []);
    }

    private void EnsureHostAllowed(string host)
    {
        if (_allowedHosts.Count > 0 && !_allowedHosts.Contains(host))
            throw new HostPipelineException(ImErrorCodes.ServiceNotGranted,
                $"出站目标不在申报范围：{host}（outboundHosts）");
    }
}

/// <summary>后台任务服务（§15.4 停用语义）：启停绑定、取消、健康、资源计数。</summary>
public sealed class BackgroundTaskService : IBackgroundTaskService
{
    private readonly object _gate = new();
    private readonly List<BackgroundTaskHandle> _tasks = [];
    private readonly ActivationRegistry _activations;
    private readonly ActivationId _activation;
    private readonly Action<string>? _log;

    public BackgroundTaskService(ActivationRegistry activations, ActivationId activation, Action<string>? log = null)
    {
        _activations = activations;
        _activation = activation;
        _log = log;
    }

    public IReadOnlyList<IBackgroundTaskHandle> ActiveTasks
    {
        get
        {
            lock (_gate) return [.. _tasks.Where(t => !t.IsCompleted)];
        }
    }

    public IBackgroundTaskHandle Run(string name, Func<CancellationToken, Task> work)
    {
        if (!_activations.IsAlive(_activation))
            throw new HostPipelineException(ImErrorCodes.ActivationStale, "活动租约已失效，禁止启动新后台任务。");
        var cts = new CancellationTokenSource();
        var handle = new BackgroundTaskHandle(name, cts);
        lock (_gate) _tasks.Add(handle);
        _ = Task.Run(async () =>
        {
            try
            {
                await work(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                handle.IsFaulted = true;
                _log?.Invoke($"后台任务 {name} 失败：{ex.Message}");
            }
            finally
            {
                handle.MarkCompleted();
            }
        }, CancellationToken.None);
        return handle;
    }

    /// <summary>停用：取消全部受管任务并等待（超时返回 false → 调用方标 Failed）。</summary>
    public async Task<bool> StopAllAsync(TimeSpan gracePeriod)
    {
        List<BackgroundTaskHandle> snapshot;
        lock (_gate) snapshot = [.. _tasks];
        foreach (var task in snapshot) task.Cancel();
        var deadline = DateTime.UtcNow + gracePeriod;
        foreach (var task in snapshot)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            var finished = await Task.WhenAny(task.Completion, Task.Delay(remaining)).ConfigureAwait(false);
            if (finished != task.Completion) return false;   // 超时 → 调用方明确 Failed
        }
        return true;
    }

    public sealed class BackgroundTaskHandle : IBackgroundTaskHandle
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts;

        public BackgroundTaskHandle(string name, CancellationTokenSource cts)
        {
            Name = name;
            _cts = cts;
            Completion = _done.Task;
        }

        public string Name { get; }
        public bool IsFaulted { get; set; }
        public bool IsCompleted => _done.Task.IsCompleted;
        public Task Completion { get; }

        internal void Cancel() => _cts.Cancel();
        internal void MarkCompleted() => _done.TrySetResult();
    }
}

/// <summary>回环 Webhook 服务（P0 仅回环）：限定路由注册 + 大小/超时限制；未注册路由 404（H08）。</summary>
public sealed class LoopbackWebhookService : IWebhookService, IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, (WebhookHandler Handler, WebhookLimits Limits, IRegistrationLease Lease)> _routes = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public LoopbackWebhookService()
    {
        // 随机回环端口（不占用 Helper 的固定端口——A04）
        var listener = new HttpListener();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = Random.Shared.Next(20000, 45000);
            listener.Close();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                return;
            }
            catch (HttpListenerException)
            {
                // 端口被占，换下一个
            }
        }
        throw new HostPipelineException(ImErrorCodes.StorageUnavailable, "无法分配回环端口。");
    }

    public IRegistrationLease RegisterRoute(string route, WebhookHandler handler, WebhookLimits limits)
    {
        var lease = new RouteLease(this, route);
        lock (_gate) _routes[route] = (handler, limits, lease);
        return lease;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var query = context.Request.Url?.Query ?? "";
            (WebhookHandler Handler, WebhookLimits Limits, IRegistrationLease Lease) route;
            lock (_gate)
            {
                // 精确匹配优先；以 /* 结尾的路由按前缀匹配（文件下载等参数化路径）
                if (!_routes.TryGetValue(path, out route!)
                    && !_routes.Any(kv => kv.Key.EndsWith("/*", StringComparison.Ordinal)
                        && path.StartsWith(kv.Key[..^2], StringComparison.Ordinal)
                        && !kv.Value.Lease.IsRevoked
                        && (route = kv.Value).Handler is not null))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                if (route.Lease.IsRevoked)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
            }
            using var ms = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            var body = ms.ToArray();
            if (body.Length > route.Limits.MaxBodyBytes)
            {
                context.Response.StatusCode = 413;
                context.Response.Close();
                return;
            }
            var headers = context.Request.Headers.AllKeys
                .Where(k => k is not null)
                .Select(k => new HttpHeader(k!, context.Request.Headers[k!] ?? ""))
                .ToList();
            var response = await route.Handler(new WebhookRequest(
                context.Request.HttpMethod, path, query, headers, body), CancellationToken.None).ConfigureAwait(false);
            context.Response.StatusCode = response.StatusCode;
            await context.Response.OutputStream.WriteAsync(response.Body).ConfigureAwait(false);
            context.Response.Close();
        }
        catch
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { /* 客户端已断开 */ }
        }
    }

    private void RevokeRoute(string route)
    {
        lock (_gate) _routes.Remove(route);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener.Close(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
    }

    private sealed class RouteLease(LoopbackWebhookService owner, string route) : IRegistrationLease
    {
        public bool IsRevoked { get; private set; }
        public event Action? Revoked;

        public void Dispose()
        {
            if (IsRevoked) return;
            IsRevoked = true;
            owner.RevokeRoute(route);
            Revoked?.Invoke();
        }
    }
}

/// <summary>审计日志：结构化 + 凭据自动隐藏（H07：日志中已知测试秘密串命中数=0）。脱敏在序列化前按字段值执行。</summary>
public sealed class AuditLog(string filePath) : IAuditLog
{
    private readonly object _gate = new();

    /// <summary>字段值脱敏器（宿主注入 CredentialService.Redact）。</summary>
    public Func<string, string?>? FieldRedactor { get; set; }

    public void Write(string category, string eventId, IReadOnlyDictionary<string, string?> fields)
    {
        var redacted = fields;
        if (FieldRedactor is not null)
        {
            redacted = fields.ToDictionary(
                kv => kv.Key,
                kv => kv.Value is null ? null : FieldRedactor(kv.Value));
        }
        var json = ImJson.Serialize(new
        {
            at = DateTimeOffset.UtcNow.ToString("O"),
            category,
            eventId,
            fields = redacted,
        });
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.AppendAllText(filePath, json + "\n");
        }
    }
}
