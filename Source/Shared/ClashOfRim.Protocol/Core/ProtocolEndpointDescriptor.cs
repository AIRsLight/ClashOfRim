namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ProtocolEndpointDescriptor
{
    public ProtocolEndpointDescriptor(
        ProtocolMessageKind kind,
        string route,
        bool requiresIdempotencyKey,
        bool requiresUserId,
        bool requiresColonyId,
        bool requiresSnapshotId,
        bool serverMustValidateSnapshot,
        ProtocolDeliverySemantics deliverySemantics)
    {
        Kind = kind;
        Route = route;
        RequiresIdempotencyKey = requiresIdempotencyKey;
        RequiresUserId = requiresUserId;
        RequiresColonyId = requiresColonyId;
        RequiresSnapshotId = requiresSnapshotId;
        ServerMustValidateSnapshot = serverMustValidateSnapshot;
        DeliverySemantics = deliverySemantics;
    }

    public ProtocolMessageKind Kind { get; }

    public string Route { get; }

    public bool RequiresIdempotencyKey { get; }

    public bool RequiresUserId { get; }

    public bool RequiresColonyId { get; }

    public bool RequiresSnapshotId { get; }

    public bool ServerMustValidateSnapshot { get; }

    public ProtocolDeliverySemantics DeliverySemantics { get; }
}
