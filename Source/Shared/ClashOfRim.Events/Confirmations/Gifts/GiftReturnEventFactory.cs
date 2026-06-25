namespace AIRsLight.ClashOfRim.Events;

public static class GiftReturnEventFactory
{
    public static AuthoritativeEvent CreateReturnEvent(
        AuthoritativeEvent giftEvent,
        DateTimeOffset createdAtUtc,
        bool originalActorOnline)
    {
        ArgumentNullException.ThrowIfNull(giftEvent);

        if (giftEvent.Type != ServerEventType.Gift || giftEvent.Payload is not GiftEventPayload giftPayload)
        {
            throw new InvalidOperationException("Only gift events can create gift return events.");
        }

        string returnIdempotencyKey = $"{giftEvent.IdempotencyKey}:return";
        var returnPayload = new GiftEventPayload(
            giftPayload.Items,
            $"Rejected gift return for {giftEvent.EventId}");

        return AuthoritativeEventFactory.Create(
            ServerEventType.GiftReturn,
            giftEvent.Target,
            giftEvent.Actor,
            returnIdempotencyKey,
            originalActorOnline,
            returnPayload,
            createdAtUtc,
            giftEvent.TargetContext);
    }
}
