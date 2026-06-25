namespace AIRsLight.ClashOfRim.Protocol;

public sealed class EventReferenceDto
{
    public EventReferenceDto(
        string eventId,
        string eventType,
        string status,
        ProtocolDeliverySemantics deliverySemantics,
        bool requiresSnapshotConfirmation)
    {
        EventId = eventId;
        EventType = eventType;
        Status = status;
        DeliverySemantics = deliverySemantics;
        RequiresSnapshotConfirmation = requiresSnapshotConfirmation;
    }

    public string EventId { get; }

    public string EventType { get; }

    public string Status { get; }

    public ProtocolDeliverySemantics DeliverySemantics { get; }

    public bool RequiresSnapshotConfirmation { get; }
}
