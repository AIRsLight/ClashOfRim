using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal static class SessionEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, LoginService login, SessionConnections connections,
        SessionPresence presence, SessionNotifications notifications, Func<ILogger> logger)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route,
            (LoginRequest request) => Results.Ok(login.Login(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapGet(ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).Route,
            (HttpContext context, IHostApplicationLifetime lifetime) => StreamAsync(context, lifetime, connections, notifications, logger))
            .WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.MaintainPresence).Route,
            (MaintainPresenceRequest request, IHostApplicationLifetime lifetime, CancellationToken cancellationToken) =>
                MaintainAsync(request, lifetime, presence, cancellationToken)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route, (LogoutRequest request) =>
        {
            var result = connections.Logout(request);
            logger().LogInformation(
                "玩家请求登出：user={UserId} colony={ColonyId} session={SessionId} disconnected={Disconnected}",
                request.UserId, request.ColonyId, request.SessionId, result.Disconnected);
            return Results.Ok(result);
        }).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    private static async Task<IResult> MaintainAsync(MaintainPresenceRequest request, IHostApplicationLifetime lifetime,
        SessionPresence presence, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        return Results.Ok(await presence.MaintainAsync(request, stop.Token));
    }

    internal static async Task StreamAsync(HttpContext context, IHostApplicationLifetime lifetime,
        SessionConnections connections, SessionNotifications notifications, Func<ILogger> logger)
    {
        string userId = context.Request.Query["userId"].ToString();
        string colonyId = context.Request.Query["colonyId"].ToString();
        string snapshotId = context.Request.Query["currentSnapshotId"].ToString();
        string sessionId = context.Request.Query["sessionId"].ToString();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest,
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("SessionStream.MissingIdentity")), stop.Token);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest,
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, "Session stream requires WebSocket."), stop.Token);
            return;
        }
        if (connections.Open(userId, colonyId, sessionId, snapshotId, out var lease) is { } denied)
        {
            await RejectAsync(context, StatusCodes.Status409Conflict, denied, stop.Token);
            return;
        }
        SessionWebSocketConnection? stream = null;
        try
        {
            stream = new(await context.WebSockets.AcceptWebSocketAsync(), connections, notifications);
            logger().LogInformation("玩家上线：user={UserId} colony={ColonyId} snapshot={SnapshotId} session={SessionId} channel=ws",
                userId, colonyId, snapshotId, sessionId);
            await stream.RunAsync(userId, colonyId, sessionId, lease!,
                Version(context, "knownNotificationVersion"), Version(context, "knownWorldConfigurationVersion"), stop.Token);
        }
        finally
        {
            // Session resources must be released even if receiving, logging, accepting or closing the socket fails.
            try
            {
                connections.Close(userId, colonyId, sessionId, lease);
                logger().LogInformation("玩家离线：user={UserId} colony={ColonyId} session={SessionId} channel=ws", userId, colonyId, sessionId);
            }
            finally { if (stream is not null) await stream.DisposeAsync(); }
        }
    }

    private static long Version(HttpContext context, string name)
        => long.TryParse(context.Request.Query[name].ToString(), out long version) ? Math.Max(0, version) : 0;

    private static Task RejectAsync(HttpContext context, int status, ProtocolResponse result, CancellationToken token)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { result }, token);
    }
}
