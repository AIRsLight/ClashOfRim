using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class ColonyAbandonment(ColonyLifecycleAccess access, SnapshotMutationCoordinator mutations,
    IColonySnapshotIndexStore snapshots, ColonyDeletion deletion, WorldConfigurationRegistry world,
    WorldConfigurationDelivery delivery, WorldMarkerDelivery markers, Func<ILogger> logger)
{
    internal AbandonPlayerColonyResponse Abandon(AbandonPlayerColonyRequest request)
    {
        AbandonPlayerColonyResponse Reject(ProtocolResponse response, bool includeWorld = false)
            => new(response, 0, 0, 0, 0, includeWorld ? delivery.Build(world.Current) : null, null);
        if (request.ProtocolVersion != ProtocolApiVersion.Current)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, Format("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("AbandonColony.MissingRequest")));
        ProtocolResponse? Authorize() => access.Authorize(request.AuthToken, request.UserId, request.ColonyId);
        if (Authorize() is { } invalid) return Reject(invalid);
        ColonyDeletionResult result;
        DateTimeOffset nowUtc;
        using (var mutation = mutations.Enter(Authorize))
        {
            if (mutation.Rejection is { } denied) return Reject(denied);
            nowUtc = DateTimeOffset.UtcNow;
            var latest = snapshots.GetLatest(request.UserId, request.ColonyId);
            if (!string.IsNullOrWhiteSpace(request.CurrentSnapshotId) && latest is not null && latest.Identity.SnapshotId != request.CurrentSnapshotId)
                return Reject(ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, Format("AbandonColony.SnapshotNotLatest")), true);
            result = deletion.DeleteUnderMutation(request.UserId, request.ColonyId, latest, nowUtc);
        }
        var configuration = delivery.Build(result.RemovedSites.Session.WorldConfiguration);
        var worldMarkers = markers.Build(request.UserId, request.ColonyId, nowUtc);
        logger().LogInformation(
            "殖民地废弃完成：user={UserId} colony={ColonyId} snapshotRemoved={SnapshotRemoved} removedSites={RemovedSites} removedEvents={RemovedEvents} removedRuntimeMarkers={RemovedRuntimeMarkers}",
            request.UserId, request.ColonyId, result.SnapshotRemoved, result.RemovedSites.RemovedCount, 0, result.RemovedRuntimeMarkers);
        return new(ProtocolResponse.Ok(Format("AbandonColony.Completed")), result.SnapshotRemoved ? 1 : 0,
            result.RemovedSites.RemovedCount, 0, result.RemovedRuntimeMarkers, configuration, ProtocolDtoMapper.ToDto(worldMarkers));
    }
}
