using System;
using System.Collections.Generic;
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

    // Retained for the explicit RimTest action protocol; game Gizmos use domain bindings.
    internal void OpenCaravanGiftDeliveryMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, bool forcedDelivery)
        => giftActions.OpenMenu(caravan, targets, forcedDelivery);
    internal void StartCreateGiftFromCaravan(Caravan caravan, ModWorldMapMarkerDto target,
        IReadOnlyList<TradeOfferSelection> selections, bool forcedDelivery, string? message)
        => giftActions.SendCaravan(caravan, ClientGiftActions.Target(target), selections, forcedDelivery, message);

    internal void OpenCaravanTradeFulfillmentMenu(Caravan caravan, IReadOnlyList<ModTradeOrderSummaryDto> orders) => tradeActions.OpenCaravanMenu(caravan, orders);

    internal void StartFulfillTradeOrderByDropPod(ModTradeOrderSummaryDto order) => tradeActions.FulfillByDropPod(order);

    internal string FormatWorldMapTargetLabel(ModWorldMapMarkerDto marker)
        => ClientWorldTargetPresentation.Label(marker);
}
