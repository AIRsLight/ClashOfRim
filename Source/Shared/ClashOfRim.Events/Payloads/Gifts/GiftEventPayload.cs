namespace AIRsLight.ClashOfRim.Events;

public sealed record GiftEventPayload(
    IReadOnlyList<EventThingReference> Items,
    string? Message,
    string? DeliveryKind = null) : LedgerEventPayload
{
    public const string ForcedDeliveryKind = "Forced";

    public bool IsForcedDelivery =>
        string.Equals(DeliveryKind, ForcedDeliveryKind, StringComparison.OrdinalIgnoreCase);
}
