using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public sealed record ItemDeliveryEventPayload(
    IReadOnlyList<EventThingReference> Items,
    string? Message,
    string? DeliveryKind = null,
    ItemDeliveryPurpose Purpose = ItemDeliveryPurpose.Gift,
    string? CreationRequestHash = null,
    TradeCollectionInfo? TradeCollection = null) : LedgerEventPayload
{
    public const string ForcedDeliveryKind = "Forced";

    public bool IsForcedDelivery =>
        string.Equals(DeliveryKind, ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase);

    public bool IsTradeDelivery =>
        Purpose is ItemDeliveryPurpose.TradeCompletedOwnerDelivery
            or ItemDeliveryPurpose.TradeCompletedAcceptorDelivery;

    public bool IsTradeReturn =>
        Purpose is ItemDeliveryPurpose.TradeExpiredOwnerReturn
            or ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn
            or ItemDeliveryPurpose.TradeCancelledOwnerReturn
            or ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn
            or ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn;
}

// A completed trade's owner proceeds remain in escrow until claimed or due.
// Persist the deadline at fulfillment so retries/configuration changes cannot reset it.
public sealed record TradeCollectionInfo(string TradeOrderId, DateTimeOffset? ExpiresAtUtc);
