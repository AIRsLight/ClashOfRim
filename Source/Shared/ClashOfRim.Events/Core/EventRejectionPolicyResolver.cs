namespace AIRsLight.ClashOfRim.Events;

public static class EventRejectionPolicyResolver
{
    public static EventRejectionPolicy Resolve(ServerEventType type, LedgerEventPayload payload)
    {
        return type switch
        {
            ServerEventType.ItemDelivery when payload is ItemDeliveryEventPayload
                { Purpose: ItemDeliveryPurpose.Gift, IsForcedDelivery: false } => EventRejectionPolicy.RejectableByTarget,
            ServerEventType.AllianceRequest => EventRejectionPolicy.RejectableByTarget,
            ServerEventType.PeaceRequest => EventRejectionPolicy.RejectableByTarget,
            ServerEventType.SupportPawn when payload is SupportPawnEventPayload support && support.TemporaryControl => EventRejectionPolicy.RejectableByTarget,
            _ => EventRejectionPolicy.NotRejectable
        };
    }
}
