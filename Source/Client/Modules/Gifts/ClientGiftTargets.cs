using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Gifts;

internal static class ClientGiftTargets
{
    internal static IReadOnlyList<ModWorldMapMarkerDto> Find(ClashOfRimClientNetworkContext context,
        ClientGiftCapabilities policy, IEnumerable<ModWorldMapMarkerDto> markers, int tile, int layer, bool forced)
    {
        if (!policy.GiftsEnabled || forced && !policy.PvpEnabled || !context.IsConfigured || string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
            return Array.Empty<ModWorldMapMarkerDto>();
        return markers.Where(m => m.Kind == "TradeableColony" && (!forced || m.CanRaid)
            && m.Tile == tile && m.TileLayerId == layer && (m.OwnerUserId != context.UserId || m.OwnerColonyId != context.ColonyId)
            && !string.IsNullOrWhiteSpace(m.OwnerUserId) && !string.IsNullOrWhiteSpace(m.OwnerColonyId)
            && !string.IsNullOrWhiteSpace(m.SnapshotId) && !string.IsNullOrWhiteSpace(m.MapId))
            .OrderBy(m => m.OwnerUserId, StringComparer.Ordinal).ThenBy(m => m.OwnerColonyId, StringComparer.Ordinal).ToArray();
    }
}
