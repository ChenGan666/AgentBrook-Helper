using System.Text.Json;
using AgentBrook.A2A.Transport.Hosting;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace AgentBrook.A2A.Transport.Protocol;

public static class A2AEndpointMapper
{
    public static void Map(IEndpointRouteBuilder endpoints, A2AHost host)
    {
        var requestHandler = endpoints.ServiceProvider.GetRequiredService<AgentBrookA2ARequestHandler>();
        var extensions = endpoints.ServiceProvider.GetRequiredService<AgentBrookExtensionHandler>();
        var card = host.AgentCard;

        endpoints.MapGet("/.well-known/agent.json", () => Results.Json(card, A2AJsonUtilities.DefaultOptions));
        endpoints.MapWellKnownAgentCard(card);
        endpoints.MapHttpA2A(requestHandler, card, "");
        endpoints.MapA2A(requestHandler, "/a2a/json-rpc");

        endpoints.MapPost("/agentbrook/v1/pairing/request", async (PairingRequestDto dto) => await host.Pairing.RequestAsync(dto));
        endpoints.MapPost("/agentbrook/v1/pairing/confirm", async (PairingConfirmDto dto) => await host.Pairing.ConfirmAsync(dto));

        var api = endpoints.MapGroup("/agentbrook/v1");
        api.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            if (httpContext.Items["A2AAuthenticatedNodeId"] is null)
            {
                return Results.Problem(statusCode: 401);
            }
            return await next(context);
        });

        api.MapGet("/profile", async () => await host.Pairing.GetProfileAsync());
        api.MapGet("/presence", () => host.Presence.GetLocalPresence());
        api.MapPost("/presence/heartbeat", async (PresenceDto dto) => await host.Presence.ReceiveHeartbeatAsync(dto));
        api.MapPost("/sessions/{sessionId}/events", async (string sessionId, [FromBody] List<SessionEventDto> events, CancellationToken ct = default) => await extensions.AppendRemoteEventsAsync(sessionId, events, ct));
        api.MapGet("/sessions/{sessionId}/events", async (string sessionId, long afterSequence = 0, CancellationToken ct = default) => await extensions.ReadEventsAsync(sessionId, afterSequence, ct));
        api.MapPost("/groups/{groupId}/events", async (string groupId, [FromBody] List<SessionEventDto> events, CancellationToken ct = default) => await extensions.AppendRemoteGroupEventsAsync(groupId, events, ct));
        api.MapGet("/groups/{groupId}/events", async (string groupId, long afterSequence = 0, CancellationToken ct = default) => await extensions.ReadGroupEventsAsync(groupId, afterSequence, ct));
        api.MapGet("/groups/{groupId}/session", async (string groupId, CancellationToken ct = default) => await extensions.GetGroupSessionAsync(groupId, ct));
        // 群组发现：成员身份取验签后的认证节点（不接受查询参数，防越权查询他人群组）
        api.MapGet("/groups/mine", async (HttpContext httpContext, CancellationToken ct = default) =>
        {
            var memberId = httpContext.Items["A2AAuthenticatedNodeId"] as string
                ?? throw new InvalidOperationException("Unauthenticated group discovery.");
            return Results.Json(await extensions.GetMemberGroupSessionsAsync(memberId, ct));
        });
        api.MapPost("/tasks", async ([FromBody] TaskManifestDto dto, CancellationToken ct = default) =>
        {
            var taskId = await extensions.CreateLocalTaskAsync(dto, ct);
            return Results.Json(new { taskId });
        });
        api.MapGet("/tasks/{taskId}", async (string taskId, CancellationToken ct = default) => await extensions.GetTaskAsync(taskId, ct));
        api.MapPost("/tasks/{taskId}/accept", async (string taskId, CancellationToken ct = default) => { await extensions.AcceptTaskAsync(taskId, ct); return Results.Ok(); });
        api.MapPost("/ratings", async ([FromBody] RatingDto dto, CancellationToken ct = default) => { await extensions.SaveRatingAsync(dto, ct); return Results.Ok(); });
        // 解除配对通知：受保护端点（须验签），通知方=当前已认证节点。
        api.MapPost("/pairing/revoke", async (HttpContext httpContext, CancellationToken ct) =>
        {
            var revoker = httpContext.Items["A2AAuthenticatedNodeId"] as string
                ?? throw new InvalidOperationException("Unauthenticated revoke notice.");
            await extensions.RevokePairingByPeerAsync(revoker, ct);
            return Results.Ok();
        });
        api.MapPost("/artifacts/offers", async ([FromBody] ArtifactOfferDto dto, CancellationToken ct = default) => await extensions.CreateArtifactOfferAsync(dto, ct));
        api.MapPost("/artifacts/{artifactId}/content", async (string artifactId, IFormFile file, CancellationToken ct = default) => Results.Json(await extensions.UploadArtifactAsync(artifactId, file, ct))).DisableAntiforgery();
        api.MapGet("/artifacts/{artifactId}/content", async (string artifactId, long? offset, long? length, CancellationToken ct = default) => await extensions.DownloadArtifactAsync(artifactId, offset, length, ct));
    }
}
