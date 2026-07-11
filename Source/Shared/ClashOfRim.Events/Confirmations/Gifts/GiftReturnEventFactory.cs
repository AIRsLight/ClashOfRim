using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public static class GiftReturnEventFactory
{
    public static AuthoritativeEvent CreateReturnEvent(
        AuthoritativeEvent giftEvent,
        DateTimeOffset createdAtUtc,
        bool originalActorOnline)
    {
        ArgumentNullException.ThrowIfNull(giftEvent);

        if (giftEvent.Type != ServerEventType.ItemDelivery
            || giftEvent.Payload is not ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.Gift } giftPayload)
        {
            throw new InvalidOperationException("Only gift events can create gift return events.");
        }

        string returnIdempotencyKey = $"{giftEvent.IdempotencyKey}:return";
        var returnPayload = new ItemDeliveryEventPayload(
            giftPayload.Items,
            $"Rejected gift return for {giftEvent.EventId}",
            Purpose: ItemDeliveryPurpose.RejectedGiftReturn);

        return AuthoritativeEventFactory.Create(
            ServerEventType.ItemDelivery,
            giftEvent.Target,
            giftEvent.Actor,
            returnIdempotencyKey,
            originalActorOnline,
            returnPayload,
            createdAtUtc,
            giftEvent.TargetContext);
    }
}
