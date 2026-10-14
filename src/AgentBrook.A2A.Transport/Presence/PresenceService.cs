using System.Collections.Concurrent;
using System.Timers;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Transport.Client;
using AgentBrook.A2A.Transport.Hosting;
using AgentBrook.A2A.Transport.Protocol;

namespace AgentBrook.A2A.Transport.Presence;

public sealed class PresenceService : IDisposable
{
    private readonly NodeIdentityService _identity;
    private readonly A2AStore _store;
    private readonly A2AClientService _client;
    private readonly A2AHostOptions _options;
    private readonly ConcurrentDictionary<string, PresenceSnapshot> _remotePresences = new();
    private readonly System.Timers.Timer _heartbeatTimer;
    private long _sequence;
    private PresenceAvailability _availability = PresenceAvailability.Online;
    private WorkState _workState = WorkState.Idle;
    private AgentReadiness _agentState = AgentReadiness.Starting;
    private MoodState? _mood;
    private int _runningTasks;
    private int _queuedTasks;
    private int _activeGroupCount;

    public PresenceService(NodeIdentityService identity, A2AStore store, A2AClientService client, A2AHostOptions options)
    {
        _identity = identity;
        _store = store;
        _client = client;
        _options = options;
        _heartbeatTimer = new System.Timers.Timer(_options.PresenceHeartbeatInterval.TotalMilliseconds);
        _heartbeatTimer.Elapsed += async (_, _) => await SendHeartbeatAsync();
        _heartbeatTimer.AutoReset = true;
    }

    public PresenceSnapshot GetLocalPresence()
    {
        return new PresenceSnapshot
        {
            NodeId = _identity.Identity.NodeId,
            InstanceId = _identity.Identity.InstanceId,
            Sequence = Interlocked.Increment(ref _sequence),
            ObservedAt = DateTimeOffset.UtcNow,
            Availability = _availability,
            WorkState = _workState,
            AgentState = _agentState,
            Mood = _mood,
            Load = new PresenceLoad
            {
                RunningTasks = _runningTasks,
                QueuedTasks = _queuedTasks,
                Capacity = _options.MaxConcurrentRemoteTasks
            },
            ActiveGroupCount = _activeGroupCount,
            ProfileVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    public void UpdateLocalState(
        PresenceAvailability? availability = null,
        WorkState? workState = null,
        AgentReadiness? agentState = null,
        MoodState? mood = null,
        int? runningTasks = null,
        int? queuedTasks = null,
        int? activeGroupCount = null)
    {
        if (availability.HasValue) _availability = availability.Value;
        if (workState.HasValue) _workState = workState.Value;
        if (agentState.HasValue) _agentState = agentState.Value;
        if (mood is not null) _mood = mood;
        if (runningTasks.HasValue) _runningTasks = Math.Max(0, runningTasks.Value);
        if (queuedTasks.HasValue) _queuedTasks = Math.Max(0, queuedTasks.Value);
        if (activeGroupCount.HasValue) _activeGroupCount = Math.Max(0, activeGroupCount.Value);
    }

    public async Task ReceiveHeartbeatAsync(PresenceDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.NodeId))
        {
            return;
        }
        var snapshot = dto.ToCore();
        _remotePresences[dto.NodeId] = snapshot;
        var peer = await _store.GetPeerAsync(dto.NodeId);
        if (peer is not null)
        {
            peer.LastSeenAt = DateTimeOffset.UtcNow;
            await _store.UpsertPeerAsync(peer);
        }
    }

    public IReadOnlyDictionary<string, PresenceSnapshot> GetRemotePresences()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - _options.PresenceOfflineAfter;
        return _remotePresences
            .Where(p => p.Value.ObservedAt > cutoff)
            .ToDictionary(p => p.Key, p => p.Value);
    }

    public bool IsOnline(string nodeId)
    {
        if (!_remotePresences.TryGetValue(nodeId, out var presence))
        {
            return false;
        }
        return presence.Availability != PresenceAvailability.Offline &&
               DateTimeOffset.UtcNow - presence.ObservedAt < _options.PresenceOfflineAfter;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _heartbeatTimer.Start();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _heartbeatTimer.Stop();
    }

    public void Dispose()
    {
        _heartbeatTimer.Stop();
        _heartbeatTimer.Dispose();
    }

    private async Task SendHeartbeatAsync()
    {
        var peers = await _store.GetPeersAsync();
        var presence = GetLocalPresence();
        foreach (var peer in peers.Where(p => p.TrustState == PeerTrustState.Trusted && p.Endpoints.Count > 0))
        {
            // 多网卡机器的对端会有多个端点（不同网段），逐个尝试直到成功；
            // 成功的端点提到首位，下次心跳直接命中。
            foreach (var endpoint in peer.Endpoints.ToList())
            {
                try
                {
                    await _client.SendPresenceAsync(new Uri(endpoint), PresenceDto.FromCore(presence));
                    if (!string.Equals(peer.Endpoints.FirstOrDefault(), endpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        peer.Endpoints.Remove(endpoint);
                        peer.Endpoints.Insert(0, endpoint);
                        await _store.UpsertPeerAsync(peer);
                    }
                    break;
                }
                catch
                {
                    // 尝试下一个端点。
                }
            }
        }
    }
}
