namespace AgentBrook.A2A.Core;

public sealed class A2AOptions
{
    public string DataDirectory { get; set; } = Path.Combine("workspace", "a2a");
    public string DisplayName { get; set; } = Environment.MachineName;
    public int ListenPort { get; set; }
    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxArtifactStoreBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public int MaxConcurrentRemoteTasks { get; set; } = 1;
    public TimeSpan PresenceHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PresenceStaleAfter { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PresenceOfflineAfter { get; set; } = TimeSpan.FromSeconds(30);
    public int OutboxMaxAttempts { get; set; } = 8;
    public TimeSpan OutboxBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public string DatabasePath => Path.Combine(DataDirectory, "a2a.db");
    public string IdentityDirectory => Path.Combine(DataDirectory, "identity");
    public string ArtifactDirectory => Path.Combine(DataDirectory, "artifacts");
}
