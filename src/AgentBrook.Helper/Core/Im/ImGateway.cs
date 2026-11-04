using System.Net;
using System.Text;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;

namespace AgentBrook.Helper.Core.Im;

/// <summary>注册进网关的路由（前缀通配或精确）。</summary>
public sealed record ImGatewayRoute(string Pattern, Func<ImGatewayRequest, CancellationToken, Task<ImGatewayResponse>> Handler, long MaxBodyBytes, TimeSpan Timeout, IRegistrationLease Lease);

/// <summary>网关请求（47852 监听器的 /im/* 段；与插件契约的 WebhookRequest 同形）。</summary>
public sealed record ImGatewayRequest(string Method, string Path, string Query, IReadOnlyList<(string Name, string Value)> Headers, byte[] Body);

/// <summary>网关响应。</summary>
public sealed record ImGatewayResponse(int StatusCode, byte[] Body);

/// <summary>可撤销租约（停用/卸载即撤销，旧回调 404）。</summary>
public sealed class ImGatewayLease : IRegistrationLease
{
    private readonly ImGateway _owner;
    private readonly string _pattern;

    internal ImGatewayLease(ImGateway owner, string pattern) { _owner = owner; _pattern = pattern; }

    public bool IsRevoked { get; private set; }
    public event Action? Revoked;

    public void Dispose()
    {
        if (IsRevoked) return;
        IsRevoked = true;
        _owner.Remove(_pattern);
        Revoked?.Invoke();
    }
}

/// <summary>
/// IM 网关（S8 任务5）：挂在既有 47852 监听器（LocalApiServer）上的 /im/* 路由表——
/// <b>不新增第二个监听器</b>、不扩大回环绑定范围。BridgeChannel 的入站/文件下载路由经
/// <see cref="HelperWebhookService"/> 注册到这里；未注册路径原样放行给 LocalApiServer 既有处理。
/// </summary>
public sealed class ImGateway
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ImGatewayRoute> _routes = [];
    public string BaseUrl { get; } = "http://127.0.0.1:47852";

    public int RouteCount { get { lock (_gate) return _routes.Count; } }

    internal void Remove(string pattern)
    {
        lock (_gate) _routes.Remove(pattern);
    }

    internal void RegisterInternal(string pattern,
        Func<ImGatewayRequest, CancellationToken, Task<ImGatewayResponse>> handler,
        long maxBodyBytes, TimeSpan timeout, IRegistrationLease lease)
    {
        lock (_gate) _routes[pattern] = new ImGatewayRoute(pattern, handler, maxBodyBytes, timeout, lease);
    }

    /// <summary>是否可处理该路径（供 LocalApiServer 分流判断）。</summary>
    public bool CanHandle(string absolutePath)
    {
        lock (_gate)
        {
            if (_routes.ContainsKey(absolutePath)) return true;
            return _routes.Any(kv => kv.Key.EndsWith("/*", StringComparison.Ordinal)
                && absolutePath.StartsWith(kv.Key[..^2], StringComparison.Ordinal)
                && !kv.Value.Lease.IsRevoked);
        }
    }

    /// <summary>分流处理；返回 false = 无匹配路由（调用方按既有 404 语义处理）。</summary>
    public Task<bool> TryHandleAsync(HttpListenerContextSlim ctx, CancellationToken cancellationToken)
        => TryHandleCoreAsync(ctx, cancellationToken);

    public async Task<bool> TryHandleCoreAsync(HttpListenerContextSlim ctx, CancellationToken cancellationToken)
    {
        ImGatewayRoute route;
        lock (_gate)
        {
            route =
                _routes.TryGetValue(ctx.Path, out var exact) && !exact.Lease.IsRevoked ? exact
                : _routes.FirstOrDefault(kv => kv.Key.EndsWith("/*", StringComparison.Ordinal)
                    && ctx.Path.StartsWith(kv.Key[..^2], StringComparison.Ordinal)
                    && !kv.Value.Lease.IsRevoked).Value;
            if (route is null) return false;
        }
        if (ctx.Body.Length > route.MaxBodyBytes)
        {
            await WriteAsync(ctx, 413, "{\"code\":1,\"message\":\"payload too large\"}", cancellationToken).ConfigureAwait(false);
            return true;
        }
        var request = new ImGatewayRequest(ctx.Method, ctx.Path, ctx.Query, ctx.Headers, ctx.Body);
        var response = await route.Handler(request, cancellationToken).ConfigureAwait(false);
        await WriteAsync(ctx, response.StatusCode, Encoding.UTF8.GetString(response.Body), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task WriteAsync(HttpListenerContextSlim ctx, int status, string json, CancellationToken ct)
    {
        await ctx.WriteResponseAsync(status, "application/json", Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
    }
}

/// <summary>LocalApiServer → 网关的瘦上下文（解耦 System.Net.HttpListener 与测试）。</summary>
public sealed record HttpListenerContextSlim(
    string Method, string Path, string Query,
    IReadOnlyList<(string Name, string Value)> Headers, byte[] Body,
    Func<int, string, byte[], CancellationToken, Task> WriteResponseAsync)
{
    public ImGatewayRequest ToRequest() => new(Method, Path, Query, Headers, Body);
}

/// <summary>System.Net.HttpListener 上下文的瘦包装（LocalApiServer 分流用）。</summary>
public sealed record ImGatewayRequestSlim(
    string Method, string Path, string Query,
    IReadOnlyList<(string Name, string Value)> Headers, byte[] Body,
    System.Net.HttpListenerContext Raw)
{
    public ImGatewayRequest ToGatewayRequest() => new(Method, Path, Query, Headers, Body);

    public HttpListenerContextSlim ToGatewayContext() => new(Method, Path, Query, Headers, Body,
        async (status, contentType, body, ct) =>
        {
            Raw.Response.StatusCode = status;
            Raw.Response.ContentType = contentType;
            await Raw.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
            Raw.Response.Close();
        });
}

/// <summary>
/// IWebhookService 的 Helper 实现：路由注册到 ImGateway（共用 47852 监听器）。
/// BridgeChannel.StartAsync 经它注册入站/文件路由——Helper 组合下不再自建监听器。
/// </summary>
public sealed class HelperWebhookService : IWebhookService
{
    private readonly ImGateway _gateway;
    public string BaseUrl => _gateway.BaseUrl;

    public HelperWebhookService(ImGateway gateway) => _gateway = gateway;

    public IRegistrationLease RegisterRoute(string route, WebhookHandler handler, WebhookLimits limits)
    {
        var lease = new ImGatewayLease(_gateway, route);
        async Task<ImGatewayResponse> Adapted(ImGatewayRequest req, CancellationToken ct)
        {
            var r = await handler(new WebhookRequest(req.Method, req.Path, req.Query, req.Headers
                .Select(h => new HttpHeader(h.Name, h.Value)).ToList(), req.Body), ct).ConfigureAwait(false);
            return new ImGatewayResponse(r.StatusCode, r.Body);
        }
        _gateway.RegisterInternal(route, Adapted, limits.MaxBodyBytes, limits.Timeout, lease);
        return lease;
    }
}
