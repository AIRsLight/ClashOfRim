namespace AIRsLight.ClashOfRim.Events;

public enum ItemDeliveryApplicationConfirmationResultKind
{
    Accepted,
    AlreadyApplied,
    EventNotFound,
    NotItemDeliveryEvent,
    NotTarget,
    NotDelivered,
    RejectedByTarget,
    SnapshotIdentityMismatch,
    SnapshotBaseMismatch,
    NotAnchored
}
