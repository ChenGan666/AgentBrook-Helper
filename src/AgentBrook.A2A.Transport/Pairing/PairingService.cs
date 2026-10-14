using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Transport.Hosting;
using AgentBrook.A2A.Transport.Protocol;

namespace AgentBrook.A2A.Transport.Pairing;

/// <summary>配对确认提示：展示对方名称/图标与两端一致的短验证码，由本机用户决定是否信任。</summary>
public sealed record PairingPrompt(
    string PeerNodeId,
    string PeerDisplayName,
    string PeerIcon,
    string ShortCode,
    bool AsResponder);


public sealed class PairingService
{
    private readonly NodeIdentityService _identity;
    private readonly A2AStore _store;
    private readonly A2AHost _host;
    private readonly ConcurrentDictionary<string, PairingState> _pending = new();

    /// <summary>响应方用户对本发起方的审批结果（RequestAsync 时征询，ConfirmAsync 时消费）。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _responderApprovals = new();

    /// <summary>审批超时：超时未确认视为拒绝，避免半开配对长期挂起。</summary>
    public static TimeSpan ApprovalTimeout { get; } = TimeSpan.FromSeconds(120);

    public PairingService(NodeIdentityService identity, A2AStore store, A2AHost host)
    {
        _identity = identity;
        _store = store;
        _host = host;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task<ProfileDto> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var profile = _host.ProfileProvider?.Invoke() ?? new PeerProfile
        {
            NodeId = _identity.Identity.NodeId,
            DisplayName = _identity.Identity.DisplayName,
            Icon = _identity.Identity.Icon,
            AgentVersion = "1.0.0",
            Platform = Environment.OSVersion.Platform.ToString(),
            Limits = new PeerLimits
            {
                MaxConcurrentTasks = _host.Options.MaxConcurrentRemoteTasks,
                MaxArtifactBytes = _host.Options.MaxArtifactBytes
            },
            ProfileVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        profile.NodeId = _identity.Identity.NodeId;
        profile.DisplayName ??= _identity.Identity.DisplayName;
        profile.Icon ??= _identity.Identity.Icon;
        profile.ReputationSummary = await _store.GetReputationSummaryAsync(_identity.Identity.NodeId);
        return ProfileDto.FromCore(profile);
    }

    public async Task<PairingResponseDto> RequestAsync(PairingRequestDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.InitiatorNodeId) || string.IsNullOrWhiteSpace(dto.InitiatorPublicKeyBase64))
        {
            throw new ArgumentException("Invalid pairing request.");
        }

        var initiatorKey = Convert.FromBase64String(dto.InitiatorPublicKeyBase64);
        var expectedNodeId = NodeIdentityService.DeriveNodeId(initiatorKey);
        if (!string.Equals(expectedNodeId, dto.InitiatorNodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Initiator nodeId does not match public key.");
        }

        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challengeBytes = Encoding.UTF8.GetBytes(challenge);
        var challengeSignature = Convert.ToBase64String(_identity.Sign(challengeBytes));
        var shortCode = ComputeShortCode(dto.InitiatorNodeId, _identity.Identity.NodeId, challenge);

        var state = new PairingState
        {
            InitiatorNodeId = dto.InitiatorNodeId,
            InitiatorPublicKeyBase64 = dto.InitiatorPublicKeyBase64,
            Challenge = challenge,
            ShortCode = shortCode
        };
        _pending[dto.InitiatorNodeId] = state;

        // §6.4 双端确认：响应方本机用户现在就会看到配对请求卡（对方名称/图标 + 两端一致的验证码）。
        var approval = _responderApprovals.GetOrAdd(
            dto.InitiatorNodeId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        var hook = _host.PairingApprovalHook;
        if (hook is not null)
        {
            _ = Task.Run(async () =>
            {
                bool approved;
                try
                {
                    approved = await hook(new PairingPrompt(
                        dto.InitiatorNodeId, dto.InitiatorDisplayName ?? dto.InitiatorNodeId,
                        dto.InitiatorIcon ?? "🤖", shortCode, AsResponder: true));
                }
                catch
                {
                    approved = false;
                }
                approval.TrySetResult(approved);
            });
        }
        else
        {
            approval.TrySetResult(true);   // 无 UI 宿主（测试/演示）：保持自动确认
        }

        var peer = await _store.GetPeerAsync(dto.InitiatorNodeId) ?? new PeerNode { NodeId = dto.InitiatorNodeId };
        peer.DisplayName = dto.InitiatorDisplayName ?? dto.InitiatorNodeId;
        peer.Icon = dto.InitiatorIcon ?? peer.Icon;
        peer.PublicKeyBase64 = dto.InitiatorPublicKeyBase64;
        peer.TrustState = PeerTrustState.PairingPending;
        if (!string.IsNullOrWhiteSpace(dto.InitiatorEndpoint))
        {
            // 发起方自报的端点来自其当次真实监听，置为首选。
            peer.Endpoints.RemoveAll(x => string.Equals(x, dto.InitiatorEndpoint, StringComparison.OrdinalIgnoreCase));
            peer.Endpoints.Insert(0, dto.InitiatorEndpoint);
        }
        await _store.UpsertPeerAsync(peer);

        return new PairingResponseDto
        {
            ResponderNodeId = _identity.Identity.NodeId,
            ResponderDisplayName = _identity.Identity.DisplayName,
            ResponderIcon = _identity.Identity.Icon,
            ResponderPublicKeyBase64 = _identity.Identity.PublicKeyBase64,
            Challenge = challenge,
            ChallengeSignature = challengeSignature,
            ShortCode = shortCode
        };
    }

    public async Task<PeerNode> PairWithAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var request = new PairingRequestDto
        {
            InitiatorNodeId = _identity.Identity.NodeId,
            InitiatorDisplayName = _identity.Identity.DisplayName,
            InitiatorIcon = _identity.Identity.Icon,
            InitiatorPublicKeyBase64 = _identity.Identity.PublicKeyBase64,
            InitiatorEndpoint = _host.Endpoint.ToString()
        };
        var response = await _host.Client.SendPairingRequestAsync(endpoint, request, cancellationToken);
        if (string.IsNullOrWhiteSpace(response.ResponderNodeId) ||
            string.IsNullOrWhiteSpace(response.ResponderPublicKeyBase64) ||
            string.IsNullOrWhiteSpace(response.Challenge) ||
            string.IsNullOrWhiteSpace(response.ChallengeSignature))
        {
            throw new InvalidOperationException("Pairing response is incomplete.");
        }
        var publicKey = Convert.FromBase64String(response.ResponderPublicKeyBase64);
        if (NodeIdentityService.DeriveNodeId(publicKey) != response.ResponderNodeId ||
            !NodeIdentityService.VerifyWithPublicKey(publicKey, Encoding.UTF8.GetBytes(response.Challenge), Convert.FromBase64String(response.ChallengeSignature)))
        {
            throw new InvalidOperationException("Pairing response identity verification failed.");
        }
        var shortCode = ComputeShortCode(_identity.Identity.NodeId, response.ResponderNodeId, response.Challenge);
        if (!string.Equals(shortCode, response.ShortCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pairing short code mismatch.");
        }

        // §6.4 双端确认（用户补充原则修订）：点"配对"即发起方的同意，发起端不再二次确认；
        // 是否建立信任由接收端用户核对验证码后决定（ConfirmAsync 侧 gating）。
        var payload = Encoding.UTF8.GetBytes($"confirm|{_identity.Identity.NodeId}|{response.ResponderNodeId}|{response.Challenge}");
        var confirm = new PairingConfirmDto
        {
            InitiatorNodeId = _identity.Identity.NodeId,
            ResponderNodeId = response.ResponderNodeId,
            Challenge = response.Challenge,
            ShortCode = shortCode,
            ConfirmationSignature = Convert.ToBase64String(_identity.Sign(payload))
        };
        var peer = await _store.GetPeerAsync(response.ResponderNodeId, cancellationToken) ?? new PeerNode { NodeId = response.ResponderNodeId };
        peer.DisplayName = response.ResponderDisplayName ?? response.ResponderNodeId;
        peer.Icon = response.ResponderIcon ?? peer.Icon;
        peer.PublicKeyBase64 = response.ResponderPublicKeyBase64;
        peer.TrustState = PeerTrustState.Trusted;
        // 刚刚配对成功的端点是最确定可达的，置于首选，避免陈旧端点继续排在前面。
        var normalizedEndpoint = endpoint.ToString().TrimEnd('/') + "/";
        peer.Endpoints.RemoveAll(x => string.Equals(x, normalizedEndpoint, StringComparison.OrdinalIgnoreCase));
        peer.Endpoints.Insert(0, normalizedEndpoint);
        await _store.UpsertPeerAsync(peer, cancellationToken);
        await _host.Client.SendPairingConfirmAsync(endpoint, confirm, cancellationToken);
        return peer;
    }

    public async Task<PeerNode> ConfirmAsync(PairingConfirmDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.InitiatorNodeId) || dto.ResponderNodeId != _identity.Identity.NodeId)
        {
            throw new ArgumentException("Invalid pairing confirmation.");
        }

        if (!_pending.TryGetValue(dto.InitiatorNodeId, out var state))
        {
            throw new InvalidOperationException("No pending pairing request.");
        }

        if (!string.Equals(state.ShortCode, dto.ShortCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Short code mismatch.");
        }

        var initiatorKey = Convert.FromBase64String(state.InitiatorPublicKeyBase64);
        var payload = Encoding.UTF8.GetBytes($"confirm|{state.InitiatorNodeId}|{_identity.Identity.NodeId}|{state.Challenge}");
        var signature = Convert.FromBase64String(dto.ConfirmationSignature ?? "");
        if (!NodeIdentityService.VerifyWithPublicKey(initiatorKey, payload, signature))
        {
            throw new InvalidOperationException("Invalid confirmation signature.");
        }

        var peer = await _store.GetPeerAsync(state.InitiatorNodeId) ?? new PeerNode { NodeId = state.InitiatorNodeId };
        peer.PublicKeyBase64 = state.InitiatorPublicKeyBase64;

        var approval = _responderApprovals.GetOrAdd(
            dto.InitiatorNodeId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (approval.Task.IsCompleted && approval.Task.Result)
        {
            // 用户已在本端确认（或无 UI 宿主自动确认）：立即建立信任。
            await FinalizePairingAsync(peer, dto.InitiatorNodeId, cancellationToken);
            return peer;
        }

        // 用户尚未点击：先落库验证过的身份，后台等审批结果（不阻塞 HTTP，发起方 8s 超时也不会失败）。
        await _store.UpsertPeerAsync(peer, cancellationToken);
        _ = Task.Run(async () =>
        {
            var finished = await Task.WhenAny(approval.Task, Task.Delay(ApprovalTimeout));
            var latest = await _store.GetPeerAsync(state.InitiatorNodeId);
            if (latest is null) return;
            if (finished == approval.Task && approval.Task.Result)
            {
                await FinalizePairingAsync(latest, state.InitiatorNodeId, CancellationToken.None);
            }
            else
            {
                _pending.TryRemove(state.InitiatorNodeId, out _);
                _responderApprovals.TryRemove(state.InitiatorNodeId, out _);
            }
        });
        return peer;
    }

    /// <summary>审批通过后建立信任并清理配对中间状态。</summary>
    private async Task FinalizePairingAsync(PeerNode peer, string initiatorNodeId, CancellationToken cancellationToken)
    {
        peer.TrustState = PeerTrustState.Trusted;
        await _store.UpsertPeerAsync(peer, cancellationToken);
        _pending.TryRemove(initiatorNodeId, out _);
        _responderApprovals.TryRemove(initiatorNodeId, out _);
    }

    /// <summary>
    /// 单方面解除配对（用户补充原则）：本端立即置为 Revoked，历史会话/成果/评价凭证保留；
    /// 解除后对端签名请求将被拒绝，重新配对必须重新走双端确认流程。
    /// </summary>
    public async Task<PeerNode> RevokePairingAsync(string peerNodeId, CancellationToken cancellationToken = default)
    {
        var peer = await _store.GetPeerAsync(peerNodeId, cancellationToken)
            ?? throw new KeyNotFoundException($"Peer {peerNodeId} not found.");
        peer.TrustState = PeerTrustState.Revoked;
        await _store.UpsertPeerAsync(peer, cancellationToken);
        // 未完成的配对审批一并作废。
        if (_responderApprovals.TryRemove(peerNodeId, out var approval))
        {
            approval.TrySetResult(false);
        }
        _pending.TryRemove(peerNodeId, out _);
        return peer;
    }

    /// <summary>收到对端的解除通知：把对端在本端的状态同步置为 Revoked（通知本身已验签，来自原 Trusted 对端）。</summary>
    public async Task RevokeByPeerAsync(string revokerNodeId, CancellationToken cancellationToken = default)
    {
        var peer = await _store.GetPeerAsync(revokerNodeId, cancellationToken);
        if (peer is null || peer.TrustState == PeerTrustState.Revoked)
        {
            return;
        }
        peer.TrustState = PeerTrustState.Revoked;
        await _store.UpsertPeerAsync(peer, cancellationToken);
    }

    public string ComputeShortCode(string initiatorNodeId, string responderNodeId, string challenge)
    {
        var ordered = new[] { initiatorNodeId, responderNodeId }.OrderBy(x => x, StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{string.Join("|", ordered)}|pairing"));
        var value = BitConverter.ToUInt32(hash, 0) % 1000000;
        return value.ToString("D6");
    }

    private sealed class PairingState
    {
        public required string InitiatorNodeId { get; init; }
        public required string InitiatorPublicKeyBase64 { get; init; }
        public required string Challenge { get; init; }
        public required string ShortCode { get; init; }
    }
}
