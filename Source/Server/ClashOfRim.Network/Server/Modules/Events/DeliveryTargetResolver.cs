using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal sealed class DeliveryTargetResolver(IColonySnapshotIndexStore snapshots)
{
    internal EventTargetContext? ResolveLatest(
        ProtocolIdentity target,
        EventLandingMode landingMode)
    {
        if (string.IsNullOrWhiteSpace(target.UserId)
            || string.IsNullOrWhiteSpace(target.ColonyId))
        {
            return null;
        }

        LatestSnapshotRecord? snapshot = snapshots.GetLatest(
            target.UserId,
            target.ColonyId!);
        if (snapshot is null)
        {
            return null;
        }

        MapSummary? map = snapshot.Index.Maps
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.UniqueId));
        if (map is null)
        {
            return null;
        }

        return new EventTargetContext(
            map.ParentWorldObjectId,
            map.UniqueId,
            Tile: null,
            landingMode);
    }

}
