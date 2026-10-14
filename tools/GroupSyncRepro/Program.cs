using System.Text.Json;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Transport.Hosting;
using AgentBrook.A2A.Transport.Protocol;

// ─────────────────────────────────────────────────────────────────────────────
// 群组链路无头复现：定位"成员端自动交流无响应"。
// 同机起 A（协调者）+ B（成员）两个完整 A2A 宿主（扩展端点齐全），自动配对后验证：
//   ① A 建群推送 GroupCreated → B 落地群组元数据（成员端可见的前提）
//   ② 两次推送均触发 B 端 GroupEventsIngested 联动（自动交流的入口）
//   ③ 成员提交（Sequence=0）→ 协调者分配全局序号并触发协调者侧联动
//   ④ @匹配逻辑（GroupAutoTalkProcessor 同规则：连字符族归一 + 忽略大小写）
// ─────────────────────────────────────────────────────────────────────────────

var dirA = "/tmp/gat-a-" + Guid.NewGuid().ToString("N")[..8];
var dirB = "/tmp/gat-b-" + Guid.NewGuid().ToString("N")[..8];

Log($"启动 A（协调者）dataDir={dirA}");
var a = new A2AHost(new A2AHostOptions { DataDirectory = dirA, DisplayName = "测试A", Icon = "🅰️", ListenPort = 0, BindToLoopback = false });
await a.StartAsync();
a.PairingApprovalHook = _ => Task.FromResult(true);
Log($"A nodeId={a.Identity.Identity.NodeId[..12]}… endpoint={a.Endpoint}");

Log("启动 B（成员）…");
var b = new A2AHost(new A2AHostOptions { DataDirectory = dirB, DisplayName = "测试B", Icon = "🅱️", ListenPort = 0, BindToLoopback = false });
await b.StartAsync();
b.PairingApprovalHook = _ => Task.FromResult(true);
Log($"B nodeId={b.Identity.Identity.NodeId[..12]}… endpoint={b.Endpoint}");

// 联动监测（必须在推送前订阅）
var raisedB = new List<(string Type, string? Text, long Sequence)>();
b.GroupEventsIngested += (groupId, events) =>
{
    foreach (var e in events)
        raisedB.Add((e.Type.ToString(), ReadText(e.PayloadJson), e.Sequence));
};
var raisedA = new List<(string Type, string? Text, long Sequence)>();
a.GroupEventsIngested += (groupId, events) =>
{
    foreach (var e in events)
        raisedA.Add((e.Type.ToString(), ReadText(e.PayloadJson), e.Sequence));
};

// ── 配对：单向发起（与 A2ADemoPeer 一致；双向互配会把对端记录搅回 PairingPending），B 自动确认 ──
Log("配对中（A → B）…");
var pairDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
while (DateTimeOffset.UtcNow < pairDeadline)
{
    var pa = await a.Store.GetPeersAsync();
    if (pa.Any(p => p.TrustState == PeerTrustState.Trusted && p.Endpoints.Count > 0))
    {
        break;
    }
    try { await a.Pairing.PairWithAsync(b.Endpoint); } catch { }
    await Task.Delay(800);
}
var peerAonB = (await b.Store.GetPeersAsync()).FirstOrDefault(p => p.TrustState == PeerTrustState.Trusted);
var peerBonA = (await a.Store.GetPeersAsync()).FirstOrDefault(p => p.TrustState == PeerTrustState.Trusted);
if (peerAonB is null || peerBonA is null)
{
    Log("✖ 配对失败，终止");
    return;
}
Log($"✅ 配对完成：{peerBonA.DisplayName} ↔ {peerAonB.DisplayName}");
Log($"A 完整 nodeId = {a.Identity.Identity.NodeId}");
foreach (var p in await b.Store.GetPeersAsync())
    Log($"B peer库: node={p.NodeId} trust={p.TrustState} key={(p.PublicKeyBase64 is null ? "无" : "有")}");
Log($"B GetPeerAsync(A) = {(await b.Store.GetPeerAsync(a.Identity.Identity.NodeId))?.TrustState.ToString() ?? "未找到"}");

// ── ① A 建群（与 Helper CreateGroupAsync 相同的载荷形态）并推送 ──
var groupId = Guid.CreateVersion7().ToString("N");
var session = A2ASession.CreateGroup(groupId, a.Identity.Identity.NodeId);
session.Name = "复现测试群";
session.Status = SessionStatus.Active;
session.Members.Add(new SessionMember { NodeId = b.Identity.Identity.NodeId, Role = "participant", JoinState = MemberJoinState.Joined });
await a.Store.SaveSessionAsync(session);

var created = SessionEvent.Create(groupId, a.Identity.Identity.NodeId, SessionEventType.GroupCreated,
    new
    {
        name = session.Name,
        coordinator = a.Identity.Identity.NodeId,
        members = session.Members.Select(x => new { nodeId = x.NodeId, role = x.Role, joinState = x.JoinState.ToString() })
    },
    senderName: "测试A", senderIcon: "🅰️");
await a.Store.AppendEventAsync(created);

var human = SessionEvent.Create(groupId, a.Identity.Identity.NodeId, SessionEventType.MessagePosted,
    new { text = "@测试B 你今天还好吗？", auto = false }, senderName: "测试A", senderIcon: "🅰️");
await a.Store.AppendEventAsync(human);

var endpointB = peerBonA.Endpoints.First();   // B 的端点：来自 A 库里对 B 的记录
for (var attempt = 1; ; attempt++)
{
    try
    {
        await a.Client.AppendGroupEventsAsync(new Uri(endpointB), groupId,
            [SessionEventDto.FromCore(created), SessionEventDto.FromCore(human)]);
        Log($"A 推送成功（第 {attempt} 次尝试）");
        break;
    }
    catch (Exception ex) when (attempt < 6)
    {
        Log($"⚠️ A 推送失败（第 {attempt} 次）：{ex.Message}，1s 后重试");
        await Task.Delay(1000);
    }
}
Log("A 已推送 GroupCreated + @消息 给 B");

await Task.Delay(2500);

// ── 校验 B 端 ──
var bSession = await b.Store.GetSessionAsync(groupId);
Log(bSession is null
    ? "✖ [检查1] B 未落地群组（元数据自愈失效）"
    : $"✅ [检查1] B 已落地群组「{bSession.Name}」成员 {bSession.Members.Count} 状态 {bSession.Status}");
Log(raisedB.Any(x => x.Type == nameof(SessionEventType.GroupCreated))
    ? "✅ [检查2] B 端 GroupEventsIngested 已触发（GroupCreated）"
    : "✖ [检查2] B 端 GroupEventsIngested 未触发");
Log(raisedB.Any(x => x.Type == nameof(SessionEventType.MessagePosted))
    ? "✅ [检查2] B 端联动收到 @消息"
    : "✖ [检查2] B 端联动未收到 @消息");

// ── ③ B 以成员身份提交 Sequence=0 消息 → A 分配全局序号并联动 ──
var memberMsg = SessionEvent.Create(groupId, b.Identity.Identity.NodeId, SessionEventType.MessagePosted,
    new { text = "@测试A 我在线，一切正常", auto = false }, senderName: "测试B", senderIcon: "🅱️");
memberMsg.Sequence = 0;
var endpointA = peerAonB.Endpoints.First();
await b.Client.AppendGroupEventsAsync(new Uri(endpointA), groupId, [SessionEventDto.FromCore(memberMsg)]);
Log("B 已提交 Sequence=0 成员消息给协调者 A");

await Task.Delay(2500);
var assigned = raisedA.FirstOrDefault(x => x.Type == nameof(SessionEventType.MessagePosted));
Log(assigned.Text is null
    ? "✖ [检查3] 协调者 A 联动未收到成员提交"
    : $"✅ [检查3] 协调者 A 已分配全局序号 {assigned.Sequence} 并触发联动：「{assigned.Text}」");

// ── ④ @匹配逻辑（与 GroupAutoTalkProcessor 同规则） ──
Log(MatchSelf("@Win–Brook 回个话", "Win-Brook")
    ? "✅ [检查4] @Win–Brook（长破折号）可命中 Win-Brook"
    : "✖ [检查4] @Win–Brook（长破折号）未命中 Win-Brook");
Log(MatchSelf("@测试B 你好", "测试B") ? "✅ [检查4] 精确中文名命中" : "✖ [检查4] 精确中文名未命中");

Log("复现结束");
return;


static string? ReadText(string? payloadJson)
{
    try
    {
        using var doc = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonDocument.Parse(payloadJson);
        if (doc?.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("text", out var t)
            && t.ValueKind == JsonValueKind.String)
        {
            return t.GetString();
        }
    }
    catch (JsonException)
    {
    }
    return null;
}

static bool MatchSelf(string text, string selfName)
{
    // 与 GroupAutoTalkProcessor.IsAddressedToSelf 相同的归一化规则
    var normalizedSelf = NormalizeMention(selfName);
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
        text, "@([^\\s，,。．？！?!!、；;：:）)】》\"']+)"))
    {
        var token = NormalizeMention(m.Groups[1].Value);
        if (token.Length > 0 && token.Equals(normalizedSelf, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }
    return false;
}

static string NormalizeMention(string s) =>
    System.Text.RegularExpressions.Regex.Replace(s.Trim(), "[\\u2010\\u2011\\u2012\\u2013\\u2014\\u2015\\u2212\\uFE58\\uFF0D]", "-");

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
