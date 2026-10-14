using System.Net.Http.Json;
using System.Text.Json;
using AgentBrook.A2A.Core.Artifacts;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Transport.Protocol;
using A2A;

namespace AgentBrook.A2A.Transport.Client;

public sealed class A2AClientService
{
    private readonly NodeIdentityService _identity;
    private readonly A2AStore _store;
    private readonly HttpClient _plainHttpClient;
    private readonly HttpClient _signedHttpClient;

    public A2AClientService(NodeIdentityService identity, A2AStore store)
    {
        _identity = identity;
        _store = store;
        // A2A is peer-to-peer LAN traffic; it must never be routed through a system/PAC proxy,
        // which cannot reach the peer's LAN or loopback address and returns 502.
        // Short connect/overall timeouts so pairing or messaging an offline peer fails fast
        // instead of blocking the caller for the 100s default.
        _plainHttpClient = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _signedHttpClient = new HttpClient(new A2ASigningHandler(identity, new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }))
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public async Task<AgentCard> ResolveCardAsync(Uri baseUrl, CancellationToken cancellationToken = default)
    {
        var resolver = new A2ACardResolver(baseUrl, _plainHttpClient);
        return await resolver.GetAgentCardAsync(cancellationToken);
    }

    public async Task<AgentCard> ResolveCardAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        return await ResolveCardAsync(new Uri(endpoint), cancellationToken);
    }

    public async Task<PairingResponseDto> SendPairingRequestAsync(Uri endpoint, PairingRequestDto dto, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/pairing/request");
        var response = await _signedHttpClient.PostAsJsonAsync(url, dto, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingResponseDto>(cancellationToken))!;
    }

    public async Task SendPairingConfirmAsync(Uri endpoint, PairingConfirmDto dto, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/pairing/confirm");
        var response = await _signedHttpClient.PostAsJsonAsync(url, dto, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>通知对端"我已解除配对"（须在本地仍被对端信任时调用）。</summary>
    public async Task SendRevokeNoticeAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/pairing/revoke");
        var response = await _signedHttpClient.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SendMessageResponse> SendMessageAsync(PeerNode peer, string text, string? contextId = null, CancellationToken cancellationToken = default)
    {
        var endpoint = peer.Endpoints.FirstOrDefault() ?? throw new InvalidOperationException("Peer has no endpoint.");
        return await SendMessageAsync(new Uri(endpoint), text, contextId, cancellationToken);
    }

    public async Task<SendMessageResponse> SendMessageAsync(Uri endpoint, string text, string? contextId = null, CancellationToken cancellationToken = default, bool autoReply = false)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            // 对端在会话流中显示本 Helper 的名称与图标。
            ["abk.senderName"] = JsonSerializer.SerializeToElement(_identity.Identity.DisplayName),
            ["abk.senderIcon"] = JsonSerializer.SerializeToElement(_identity.Identity.Icon)
        };
        if (autoReply)
        {
            // 自动生成的回复：对端只记录、不再触发其自动处理（防回声循环）。
            metadata["abk.autoReply"] = JsonSerializer.SerializeToElement("true");
        }
        var request = new SendMessageRequest
        {
            Message = new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Role = Role.User,
                ContextId = contextId,
                Parts = new List<Part> { Part.FromText(text) },
                Metadata = metadata
            }
        };
        var client = new A2AHttpJsonClient(endpoint, _signedHttpClient);
        return await client.SendMessageAsync(request, cancellationToken);
    }

    public async Task<ProfileDto> GetProfileAsync(PeerNode peer, CancellationToken cancellationToken = default)
    {
        var endpoint = peer.Endpoints.FirstOrDefault() ?? throw new InvalidOperationException("Peer has no endpoint.");
        return await GetProfileAsync(new Uri(endpoint), cancellationToken);
    }

    public async Task<ProfileDto> GetProfileAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/profile");
        var response = await _signedHttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileDto>(cancellationToken))!;
    }

    public async Task SendPresenceAsync(PeerNode peer, PresenceDto presence, CancellationToken cancellationToken = default)
    {
        var endpoint = peer.Endpoints.FirstOrDefault() ?? throw new InvalidOperationException("Peer has no endpoint.");
        await SendPresenceAsync(new Uri(endpoint), presence, cancellationToken);
    }

    public async Task SendPresenceAsync(Uri endpoint, PresenceDto presence, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/presence/heartbeat");
        var response = await _signedHttpClient.PostAsJsonAsync(url, presence, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<SessionEventDto>> ReadEventsAsync(Uri endpoint, string sessionId, long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/sessions/{Uri.EscapeDataString(sessionId)}/events?afterSequence={afterSequence}");
        var response = await _signedHttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<SessionEventDto>>(cancellationToken)) ?? new();
    }

    public async Task AppendEventsAsync(Uri endpoint, string sessionId, List<SessionEventDto> events, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/sessions/{Uri.EscapeDataString(sessionId)}/events");
        var response = await _signedHttpClient.PostAsJsonAsync(url, events, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<SessionEventDto>> ReadGroupEventsAsync(Uri endpoint, string groupId, long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/groups/{Uri.EscapeDataString(groupId)}/events?afterSequence={afterSequence}");
        var response = await _signedHttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<SessionEventDto>>(cancellationToken)) ?? new();
    }

    public async Task AppendGroupEventsAsync(Uri endpoint, string groupId, List<SessionEventDto> events, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/groups/{Uri.EscapeDataString(groupId)}/events");
        var response = await _signedHttpClient.PostAsJsonAsync(url, events, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>读取群组元数据快照（成员表/群名/状态）：成员端补拉用；远端无此群返回 null。</summary>
    public async Task<SessionDto?> ReadGroupSessionAsync(Uri endpoint, string groupId, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/groups/{Uri.EscapeDataString(groupId)}/session");
        var response = await _signedHttpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SessionDto>(cancellationToken);
    }

    /// <summary>群组发现：查询远端"我（认证节点）作为成员的活跃群组"。离线错过建群推送的成员上线后据此补齐。</summary>
    public async Task<List<SessionDto>> ReadMyGroupSessionsAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/groups/mine");
        var response = await _signedHttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<SessionDto>>(cancellationToken) ?? new();
    }

    public async Task<DistributedTask> CreateTaskAsync(Uri endpoint, TaskManifestDto manifest, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/tasks");
        var response = await _signedHttpClient.PostAsJsonAsync(url, manifest, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await response.Content.ReadFromJsonAsync<TaskIdWrapper>(cancellationToken);
        var taskId = wrapper?.taskId ?? throw new InvalidOperationException("Remote task id is missing.");
        return await GetTaskAsync(endpoint, taskId, cancellationToken)
            ?? throw new InvalidOperationException("Remote task was not found after creation.");
    }

    public async Task<DistributedTask?> GetTaskAsync(Uri endpoint, string taskId, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/tasks/{Uri.EscapeDataString(taskId)}");
        var response = await _signedHttpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DistributedTask>(cancellationToken);
    }

    public async Task AcceptTaskAsync(Uri endpoint, string taskId, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/tasks/{Uri.EscapeDataString(taskId)}/accept");
        var response = await _signedHttpClient.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SaveRatingAsync(Uri endpoint, RatingDto rating, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/ratings");
        var response = await _signedHttpClient.PostAsJsonAsync(url, rating, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ArtifactDescriptor> CreateArtifactOfferAsync(Uri endpoint, ArtifactOfferDto offer, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, "/agentbrook/v1/artifacts/offers");
        var response = await _signedHttpClient.PostAsJsonAsync(url, offer, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ArtifactDescriptor>(cancellationToken))!;
    }

    public async Task<ArtifactDescriptor> UploadArtifactAsync(Uri endpoint, string artifactId, Stream content, string mediaType, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/artifacts/{Uri.EscapeDataString(artifactId)}/content");
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        form.Add(streamContent, "file", "artifact");
        var response = await _signedHttpClient.PostAsync(url, form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Artifact upload failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }
        return (await response.Content.ReadFromJsonAsync<ArtifactDescriptor>(cancellationToken))!;
    }

    private sealed class TaskIdWrapper
    {
        public string taskId { get; set; } = "";
    }

    public async Task<Stream> DownloadArtifactAsync(Uri endpoint, string artifactId, long? offset = null, long? length = null, CancellationToken cancellationToken = default)
    {
        var url = new Uri(endpoint, $"/agentbrook/v1/artifacts/{Uri.EscapeDataString(artifactId)}/content");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset.HasValue)
        {
            var range = length.HasValue ? $"bytes={offset}-{offset + length - 1}" : $"bytes={offset}-";
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset.Value, length.HasValue ? offset.Value + length.Value - 1 : null);
        }
        var response = await _signedHttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}
