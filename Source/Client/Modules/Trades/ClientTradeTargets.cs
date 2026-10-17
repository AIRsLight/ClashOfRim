using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Trades;

internal sealed record ClientTradeLocation(int Tile, int Layer);
internal static class ClientTradeTargets
{
    internal static ClientTradeLocation? Resolve(ModTradeOrderSummaryDto order, IReadOnlyList<ModWorldMapMarkerDto> markers)
    {
        var matches = markers.Where(m => m.CanTrade && m.OwnerUserId == order.Owner?.UserId
            && (string.IsNullOrWhiteSpace(order.Owner?.ColonyId) || m.OwnerColonyId == order.Owner.ColonyId));
        if (!string.IsNullOrWhiteSpace(order.TargetContext?.MapUniqueId)) matches = matches.Where(m => m.MapId == order.TargetContext.MapUniqueId);
        if (!string.IsNullOrWhiteSpace(order.TargetContext?.WorldObjectId)) matches = matches.Where(m => m.WorldObjectId == order.TargetContext.WorldObjectId);
        if (order.TargetContext?.Tile is { } tile) matches = matches.Where(m => m.Tile == tile);
        var marker = matches.OrderBy(m => m.TileLayerId).ThenBy(m => m.Tile).FirstOrDefault();
        if (marker is not null) return new(marker.Tile, marker.TileLayerId);
        // Legacy targets only carried a tile on the default world layer.
        return order.TargetContext?.Tile is { } legacyTile ? new(legacyTile, 0) : null;
    }
    internal static bool CanSelfDeliver(ModTradeOrderSummaryDto order, ClientTradeLocation? target, int tile, int layer) =>
        order.ViewerHasAccepted && order.AllowSelfPickup && !string.IsNullOrWhiteSpace(order.ViewerAcceptedMemoEventId)
        && (ClientTradeRules.IsOpen(order.Status) || string.IsNullOrWhiteSpace(order.Status)) && target?.Tile == tile && target.Layer == layer;
}
