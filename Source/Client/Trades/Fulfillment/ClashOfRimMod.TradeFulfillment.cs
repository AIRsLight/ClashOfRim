using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void ConfirmTradeAcceptorWillReceiveGoods(ModTradeOrderSummaryDto order, Action confirmedAction) => tradeActions.ConfirmGoods(order, confirmedAction);

    internal void StartAcceptTradeOrder(ModTradeOrderSummaryDto order, bool postagePaidByAcceptor) => _ = tradeMarket.AcceptAsync(order, postagePaidByAcceptor);

    internal IReadOnlyList<ModTradeOrderSummaryDto> FindFulfillableTradeOrdersForCaravan(Caravan caravan) => tradeActions.FindFulfillable(caravan);

    internal IReadOnlyList<ModWorldMapMarkerDto> FindReinforceableTargetsForCaravan(Caravan caravan)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return Array.Empty<ModWorldMapMarkerDto>();
        }

        int tile = caravan.Tile;
        lock (eventStateLock)
        {
            return lastWorldMapMarkers
                .Where(marker => marker.CanReinforce)
                .Where(marker => marker.Tile == tile)
                .Where(marker => !string.Equals(marker.OwnerUserId, settings.UserId, StringComparison.Ordinal)
                    || !string.Equals(marker.OwnerColonyId, settings.ColonyId, StringComparison.Ordinal))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerUserId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerColonyId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.SnapshotId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.MapId))
                .OrderBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
                .ThenBy(marker => marker.OwnerColonyId, StringComparer.Ordinal)
                .ToList();
        }
    }

    internal IReadOnlyList<ModWorldMapMarkerDto> FindRaidableTargetsForCaravan(Caravan caravan)
    {
        if (!pvpEnabled
            || !settings.IsConfigured
            || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return Array.Empty<ModWorldMapMarkerDto>();
        }

        int tile = caravan.Tile;
        lock (eventStateLock)
        {
            return lastWorldMapMarkers
                .Where(marker => marker.CanRaid)
                .Where(marker => marker.Tile == tile)
                .Where(marker => !string.Equals(marker.OwnerUserId, settings.UserId, StringComparison.Ordinal)
                    || !string.Equals(marker.OwnerColonyId, settings.ColonyId, StringComparison.Ordinal))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerUserId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerColonyId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.SnapshotId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.MapId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.WorldObjectId))
                .OrderBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
                .ThenBy(marker => marker.OwnerColonyId, StringComparer.Ordinal)
                .ToList();
        }
    }

    // UI compatibility entry points until M11; gift modules own policy and execution.
    internal IReadOnlyList<ModWorldMapMarkerDto> FindGiftDeliverableTargetsForCaravan(Caravan caravan)
        => giftActions.FindTargets(caravan, forced: false);
    internal IReadOnlyList<ModWorldMapMarkerDto> FindForcedGiftDeliverableTargetsForCaravan(Caravan caravan)
        => giftActions.FindTargets(caravan, forced: true);
    internal void OpenCaravanGiftDeliveryMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, bool forcedDelivery)
        => giftActions.OpenMenu(caravan, targets, forcedDelivery);
    internal void StartCreateGiftFromCaravan(Caravan caravan, ModWorldMapMarkerDto target,
        IReadOnlyList<TradeOfferSelection> selections, bool forcedDelivery, string? message)
        => giftActions.SendCaravan(caravan, ClientGiftActions.Target(target), selections, forcedDelivery, message);
    internal void StartCreateGiftFromTransportPods(string transporterKey, string targetUserId, string targetColonyId,
        string targetSnapshotId, string targetMapId, string targetWorldObjectId, PlanetTile targetTile,
        string targetLabel, IReadOnlyList<ActiveTransporterInfo> transporters, bool forcedDelivery)
        => giftActions.SendTransportPods(transporterKey, targetUserId, targetColonyId, targetSnapshotId, targetMapId,
            targetWorldObjectId, targetTile, targetLabel, transporters, forcedDelivery);
    internal bool CanStartTransportPodGiftArrival() => giftActions.CanStartArrival;

    internal void OpenCaravanTradeFulfillmentMenu(Caravan caravan, IReadOnlyList<ModTradeOrderSummaryDto> orders) => tradeActions.OpenCaravanMenu(caravan, orders);

    internal void StartFulfillTradeOrderByDropPod(ModTradeOrderSummaryDto order) => tradeActions.FulfillByDropPod(order);

    private static string FormatSupportTargetLabel(ModWorldMapMarkerDto marker)
        => ClientWorldTargetPresentation.Label(marker);

    internal string FormatWorldMapTargetLabel(ModWorldMapMarkerDto marker)
    {
        return FormatSupportTargetLabel(marker);
    }

}
