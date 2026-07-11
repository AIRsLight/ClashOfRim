using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public sealed record GiftEventPayload(
    IReadOnlyList<EventThingReference> Items,
    string? Message,
    string? DeliveryKind = null,
    GiftEventPurpose Purpose = GiftEventPurpose.Gift) : LedgerEventPayload
{
    public const string ForcedDeliveryKind = "Forced";

    public bool IsForcedDelivery =>
        string.Equals(DeliveryKind, ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase);

    public bool IsTradeDelivery =>
        Purpose is GiftEventPurpose.TradeCompletedOwnerDelivery
            or GiftEventPurpose.TradeCompletedAcceptorDelivery;

    public bool IsTradeReturn =>
        Purpose is GiftEventPurpose.TradeExpiredOwnerReturn
            or GiftEventPurpose.TradeBaselineChangedOwnerReturn
            or GiftEventPurpose.TradeCancelledOwnerReturn
            or GiftEventPurpose.TradeApplicationFailedOwnerReturn;
}
