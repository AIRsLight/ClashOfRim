using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal enum WorldSubstrateDownloadStatus { Available, InvalidRequest, Unauthorized, NotFound }
internal sealed record WorldSubstrateDownload(WorldSubstrateDownloadStatus Status, byte[]? Payload = null);

internal sealed class WorldSubstrateTransfer(AccountService accounts, SnapshotPrincipalAuthorization principals,
    WorldConfigurationRegistry world, WorldConfigurationDelivery delivery, WorldConfigurationChanges changes, Func<ILogger> logger)
{
    internal UploadWorldSubstrateResponse Upload(UploadWorldSubstrateRequest? request, byte[]? payload, string? principalToken)
    {
        UploadWorldSubstrateResponse Reject(ProtocolResponse failure) => new(failure, false, 0, 0, delivery.Build(world.Current));
        if (request is null || payload is null || request.ProtocolVersion != ProtocolApiVersion.Current
            || string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId) || string.IsNullOrWhiteSpace(request.WorldConfigurationId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("WorldConfiguration.TileGeometryMissingRequest")));
        if (!principals.TryAuthorize(principalToken, DateTimeOffset.UtcNow, out var principal)
            || !SnapshotPrincipalAuthorization.Matches(principal!, request.UserId, request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, Format("Auth.Failed")));
        var auth = accounts.Authenticate(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? Format("Steam.Failed")));
        var result = world.StoreWorldSubstrate(auth.AuthenticatedUserId, request.ColonyId, request.WorldConfigurationId, payload);
        var delivered = delivery.Build(result.WorldConfiguration);
        if (!result.Accepted)
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                Format("WorldConfiguration.TileGeometryRejected", ("REASON", result.Message))), false, result.LayerCount, result.TileCenterCount, delivered);
        // Notify once the replacement is ready; baseline submission alone hides Current.
        changes.Signal(auth.AuthenticatedUserId);
        logger().LogInformation("世界底图已提交：user={UserId} colony={ColonyId} world={WorldConfigurationId} bytes={PayloadBytes} layers={LayerCount} tileCenters={TileCenterCount}",
            auth.AuthenticatedUserId, request.ColonyId, request.WorldConfigurationId, payload.Length, result.LayerCount, result.TileCenterCount);
        return new(ProtocolResponse.Ok(Format("WorldConfiguration.TileGeometryRegistered")), true, result.LayerCount, result.TileCenterCount, delivered);
    }

    internal WorldSubstrateDownload Download(DownloadWorldSubstrateRequest request)
    {
        if (request.ProtocolVersion != ProtocolApiVersion.Current || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId) || string.IsNullOrWhiteSpace(request.WorldConfigurationId))
            return new(WorldSubstrateDownloadStatus.InvalidRequest);
        var auth = accounts.Authenticate(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId)) return new(WorldSubstrateDownloadStatus.Unauthorized);
        return world.TryGetWorldSubstrate(request.WorldConfigurationId, out var payload) && payload is not null
            ? new(WorldSubstrateDownloadStatus.Available, payload) : new(WorldSubstrateDownloadStatus.NotFound);
    }
}
