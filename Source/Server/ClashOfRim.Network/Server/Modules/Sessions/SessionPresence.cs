using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed class SessionPresence(SessionConnections connections, Func<ILogger> logger)
{
    internal async Task<MaintainPresenceResponse> MaintainAsync(MaintainPresenceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Presence.MissingIdentity")), request.UserId ?? string.Empty, false);
        if (connections.Open(request.UserId, request.ColonyId, request.SessionId, request.CurrentSnapshotId, out var presence) is { } denied)
            return new(denied, request.UserId, false);
        try
        {
            logger().LogInformation("玩家上线：user={UserId} colony={ColonyId} snapshot={SnapshotId} session={SessionId} channel=presence",
                request.UserId, request.ColonyId, request.CurrentSnapshotId, request.SessionId);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(SessionConnections.HeartbeatInterval, cancellationToken);
                    if (connections.Heartbeat(request.UserId, request.ColonyId, request.SessionId, presence!) is not null) break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            return new(ProtocolResponse.Ok(ServerLocalization.Text("Presence.Closed")), request.UserId, false);
        }
        finally
        {
            connections.Close(request.UserId, request.ColonyId, request.SessionId, presence);
            logger().LogInformation("玩家离线：user={UserId} colony={ColonyId} session={SessionId} channel=presence", request.UserId, request.ColonyId, request.SessionId);
        }
    }
}
