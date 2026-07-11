using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public sealed record ItemDeliveryEventPayload(
    IReadOnlyList<EventThingReference> Items,
    string? Message,
    string? DeliveryKind = null,
    ItemDeliveryPurpose Purpose = ItemDeliveryPurpose.Gift) : LedgerEventPayload
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
            or ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn;
}
