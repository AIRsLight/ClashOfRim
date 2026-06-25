namespace AIRsLight.ClashOfRim.Events;

public static class EventRejectionPolicyResolver
{
    public static EventRejectionPolicy Resolve(ServerEventType type, LedgerEventPayload payload)
    {
        return type switch
        {
            ServerEventType.Gift when payload is GiftEventPayload gift && gift.IsForcedDelivery => EventRejectionPolicy.NotRejectable,
            ServerEventType.Gift => EventRejectionPolicy.RejectableByTarget,
            ServerEventType.AllianceRequest => EventRejectionPolicy.RejectableByTarget,
            ServerEventType.PeaceRequest => EventRejectionPolicy.RejectableByTarget,
            ServerEventType.SupportPawn when payload is SupportPawnEventPayload support && support.TemporaryControl => EventRejectionPolicy.RejectableByTarget,
            _ => EventRejectionPolicy.NotRejectable
        };
    }
}
