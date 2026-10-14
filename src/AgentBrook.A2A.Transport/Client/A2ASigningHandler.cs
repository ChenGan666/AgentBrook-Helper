using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Transport.Security;

namespace AgentBrook.A2A.Transport.Client;

public sealed class A2ASigningHandler : DelegatingHandler
{
    private readonly NodeIdentityService _identity;

    public A2ASigningHandler(NodeIdentityService identity)
    {
        _identity = identity;
    }

    public A2ASigningHandler(NodeIdentityService identity, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _identity = identity;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = Guid.NewGuid().ToString("N");
        var path = request.RequestUri!.AbsolutePath + request.RequestUri.Query;
        var payload = SignatureHeaderParser.BuildSigningPayload(request.Method.Method, path, timestamp, nonce);
        var signature = _identity.SignPayload(payload);
        request.Headers.TryAddWithoutValidation(SignatureHeaderParser.HeaderName, SignatureHeaderParser.Build(_identity.Identity.NodeId, timestamp, nonce, signature));
        return base.SendAsync(request, cancellationToken);
    }
}
