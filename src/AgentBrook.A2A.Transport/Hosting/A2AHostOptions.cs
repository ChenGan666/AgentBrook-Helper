namespace AgentBrook.A2A.Transport.Hosting;

public sealed class A2AHostOptions
{
    public string DataDirectory { get; set; } = Path.Combine("workspace", "a2a");
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>对外展示的 Helper 图标（emoji 或图标键），随配对、Profile 与会话事件发送给其他 Helper。</summary>
    public string Icon { get; set; } = "🤖";
    public int ListenPort { get; set; }
    public bool BindToLoopback { get; set; }
    public bool UseHttps { get; set; }
    public string? HttpsCertificatePath { get; set; }
    public string? HttpsCertificatePassword { get; set; }
    public string PathBase { get; set; } = "";
    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxArtifactStoreBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public int MaxConcurrentRemoteTasks { get; set; } = 1;

    /// <summary>
    /// 局域网发现组播端口（默认 19001）。测试/隔离环境用独立端口，
    /// 避免测试节点的发现信标进入真实网络的邻居列表（组播在本机所有成员间互通）。
    /// </summary>
    public int DiscoveryPort { get; set; } = 19001;
    public TimeSpan PresenceHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PresenceStaleAfter { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PresenceOfflineAfter { get; set; } = TimeSpan.FromSeconds(30);
    public int OutboxMaxAttempts { get; set; } = 8;
    public TimeSpan OutboxBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimestampWindow { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ReplayNonceTtl { get; set; } = TimeSpan.FromMinutes(10);
    public string IdentityDirectory => Path.Combine(DataDirectory, "identity");
    public string DatabasePath => Path.Combine(DataDirectory, "a2a.db");
    public string ArtifactDirectory => Path.Combine(DataDirectory, "artifacts");
}
