using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Gifts;

internal static class ClientGiftTargets
{
    internal static string? DisabledReason(ClientGiftCapabilities policy, bool forced)
        => !policy.GiftsEnabled ? "ClashOfRim.GiftDelivery.Disabled"
            : forced && !policy.PvpEnabled ? "ClashOfRim.Raid.PvpDisabled" : null;

    internal static bool HasContext(string? user, string? colony, string? map)
        => !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(colony) && !string.IsNullOrWhiteSpace(map);

    // A disabled gift feature still has a disabled menu entry. Forced delivery
    // is offered only when the marker permits a raid and PvP is enabled.
    internal static bool CanOfferDelivery(ModWorldMapMarkerDto target, ClientGiftCapabilities policy, bool forced)
        => target.Kind == "TradeableColony" && HasContext(target.OwnerUserId, target.OwnerColonyId, target.MapId)
            && (!forced || policy.PvpEnabled && target.CanRaid);

    internal static string? TransporterRejection(ModWorldMapMarkerDto target, ClientGiftCapabilities policy, bool forced, bool menu = false)
        => DisabledReason(policy, forced) ?? ((menu ? CanOfferDelivery(target, policy, forced) : HasContext(target.OwnerUserId, target.OwnerColonyId, target.MapId))
            ? null : "ClashOfRim.GiftDelivery.StatusTargetIncomplete");

    internal static IReadOnlyList<ModWorldMapMarkerDto> Find(ClashOfRimClientNetworkContext context,
        ClientGiftCapabilities policy, IEnumerable<ModWorldMapMarkerDto> markers, int tile, int layer, bool forced)
    {
        if (DisabledReason(policy, forced) is not null || !context.IsConfigured || string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
            return Array.Empty<ModWorldMapMarkerDto>();
        return markers.Where(m => CanOfferDelivery(m, policy, forced)
            && m.Tile == tile && m.TileLayerId == layer && (m.OwnerUserId != context.UserId || m.OwnerColonyId != context.ColonyId)
            && !string.IsNullOrWhiteSpace(m.SnapshotId))
            .OrderBy(m => m.OwnerUserId, StringComparer.Ordinal).ThenBy(m => m.OwnerColonyId, StringComparer.Ordinal).ToArray();
    }
}
