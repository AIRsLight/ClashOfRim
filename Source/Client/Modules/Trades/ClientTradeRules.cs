using System;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Trades;

internal static class ClientTradeRules
{
    internal static bool IsOpen(string? status) => status is "PendingOfflineDelivery" or "ReadyForImmediateDelivery" or "Recorded";
    internal static bool IsOwnOpen(ModTradeOrderSummaryDto order, ClashOfRimClientNetworkContext context) => IsOpen(order.Status)
        && string.Equals(order.Owner?.UserId, context.UserId, StringComparison.Ordinal)
        && string.Equals(order.Owner?.ColonyId, context.ColonyId, StringComparison.Ordinal);
    internal static bool CanAct(ModTradeOrderSummaryDto order, ClashOfRimClientNetworkContext context) => IsOpen(order.Status)
        && !string.Equals(order.Owner?.UserId, context.UserId, StringComparison.Ordinal);
    internal static bool CanCancelMemo(ModTradeOrderSummaryDto order, ClashOfRimClientNetworkContext context) => CanAct(order, context)
        && order.ViewerHasAccepted && !string.IsNullOrWhiteSpace(order.ViewerAcceptedMemoEventId);
}
