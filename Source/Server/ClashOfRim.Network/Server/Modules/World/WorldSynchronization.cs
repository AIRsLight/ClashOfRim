using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldSynchronization(ColonyAccessAuthorization authorization, SnapshotMutationCoordinator mutations,
    RaidTimeouts timeouts, RuntimeWorldObjectMarkerRegistry runtime, WorldMarkerDelivery markers)
{
    internal WorldMapMarkerDeliveryDto Read(SyncWorldMapMarkersRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(request.UserId)) return new(string.Empty, now, []);
        // The existing read protocol has no credential; retain the full current delivery contract.
        timeouts.Reconcile(now);
        now = DateTimeOffset.UtcNow;
        return ProtocolDtoMapper.ToDto(markers.Build(request.UserId, request.ColonyId, now));
    }

    internal SyncRuntimeWorldObjectsResponse Replace(SyncRuntimeWorldObjectsRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        SyncRuntimeWorldObjectsResponse Reject(ProtocolResponse failure) => new(failure, request.UserId, now, 0, null);
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("RuntimeObjects.MissingIdentity")));
        ProtocolResponse? Authorize() => authorization.TryAuthorize(request.AuthToken, request.UserId, request.ColonyId,
            null, null, DateTimeOffset.UtcNow, out var failure) ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure);
        if (Authorize() is { } invalid) return Reject(invalid);
        var prepared = runtime.Prepare(request.UserId, request.ColonyId, request.SnapshotId, request.Objects);
        using var mutation = mutations.Enter(Authorize);
        if (mutation.Rejection is { } denied) return Reject(denied);
        now = DateTimeOffset.UtcNow;
        int accepted = runtime.Publish(prepared, now);
        return new(ProtocolResponse.Ok(ServerLocalization.Text("RuntimeObjects.Synced")), request.UserId, now, accepted,
            ProtocolDtoMapper.ToDto(markers.Build(request.UserId, request.ColonyId, now)));
    }
}
