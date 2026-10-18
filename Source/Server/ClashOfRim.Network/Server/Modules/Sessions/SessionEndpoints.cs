using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal static class SessionEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, LoginService login, SessionConnections connections, Func<ILogger> logger)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route,
            (LoginRequest request) => Results.Ok(login.Login(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route, (LogoutRequest request) =>
        {
            var result = connections.Logout(request);
            logger().LogInformation(
                "玩家请求登出：user={UserId} colony={ColonyId} session={SessionId} disconnected={Disconnected}",
                request.UserId, request.ColonyId, request.SessionId, result.Disconnected);
            return Results.Ok(result);
        }).WithMetadata(EndpointSnapshotRecovery.Instance);
    }
}
