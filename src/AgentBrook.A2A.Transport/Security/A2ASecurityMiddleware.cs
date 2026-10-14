using System.Collections.Concurrent;
using System.Text;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Transport.Hosting;

namespace AgentBrook.A2A.Transport.Security;

public sealed class A2ASecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly A2AStore _store;
    private readonly A2AHostOptions _options;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new();
    private DateTimeOffset _lastCleanup = DateTimeOffset.UtcNow;

    public A2ASecurityMiddleware(RequestDelegate next, A2AStore store, A2AHostOptions options)
    {
        _next = next;
        _store = store;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (IsPublicPath(path))
        {
            if (SignatureHeaderParser.TryParse(context.Request.Headers[SignatureHeaderParser.HeaderName], out var h))
            {
                context.Items["A2AAuthenticatedNodeId"] = h!.NodeId;
            }
            await _next(context);
            return;
        }

        if (!SignatureHeaderParser.TryParse(context.Request.Headers[SignatureHeaderParser.HeaderName], out var header))
        {
            Console.Error.WriteLine($"[a2a-auth] 401 缺失/非法签名头：{context.Request.Method} {path}");
            context.Response.StatusCode = 401;
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - header!.Timestamp) > _options.RequestTimestampWindow.TotalMilliseconds)
        {
            Console.Error.WriteLine($"[a2a-auth] 401 时间窗超限：node={header.NodeId[..Math.Min(12, header.NodeId.Length)]} 偏移={now - header.Timestamp}ms path={path}");
            context.Response.StatusCode = 401;
            return;
        }

        CleanupNonces();
        if (!_nonces.TryAdd(header.Nonce, DateTimeOffset.UtcNow))
        {
            Console.Error.WriteLine($"[a2a-auth] 401 nonce 重放：node={header.NodeId[..Math.Min(12, header.NodeId.Length)]} path={path}");
            context.Response.StatusCode = 401;
            return;
        }

        var peer = await _store.GetPeerAsync(header.NodeId);
        if (peer?.PublicKeyBase64 is null || peer.TrustState != PeerTrustState.Trusted)
        {
            Console.Error.WriteLine($"[a2a-auth] 401 对端未登记/未信任：node={header.NodeId[..Math.Min(12, header.NodeId.Length)]} 存在={peer is not null} 信任={peer?.TrustState.ToString() ?? "无"} path={path}");
            context.Response.StatusCode = 401;
            return;
        }

        // 客户端签名覆盖 path + query（见 A2ASigningHandler），校验必须使用同一载荷。
        var signedPath = path + context.Request.QueryString.Value;
        var payload = SignatureHeaderParser.BuildSigningPayload(context.Request.Method, signedPath, header.Timestamp, header.Nonce);
        var publicKey = Convert.FromBase64String(peer.PublicKeyBase64);
        var signature = Convert.FromBase64String(header.SignatureBase64);
        if (!NodeIdentityService.VerifyWithPublicKey(publicKey, Encoding.UTF8.GetBytes(payload), signature))
        {
            Console.Error.WriteLine($"[a2a-auth] 401 签名校验失败：node={header.NodeId[..Math.Min(12, header.NodeId.Length)]} method={context.Request.Method} signedPath={signedPath}");
            context.Response.StatusCode = 401;
            return;
        }

        context.Items["A2AAuthenticatedNodeId"] = header.NodeId;
        await _next(context);
    }

    private static bool IsPublicPath(string path)
    {
        return path.Equals("/.well-known/agent.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/.well-known/agent-card.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/agentbrook/v1/pairing/request", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/agentbrook/v1/pairing/confirm", StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupNonces()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCleanup < TimeSpan.FromMinutes(1))
        {
            return;
        }
        var cutoff = now - _options.ReplayNonceTtl;
        foreach (var pair in _nonces)
        {
            if (pair.Value < cutoff)
            {
                _nonces.TryRemove(pair.Key, out _);
            }
        }
        _lastCleanup = now;
    }
}
