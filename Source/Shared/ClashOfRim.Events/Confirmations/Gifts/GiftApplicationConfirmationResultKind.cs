namespace AIRsLight.ClashOfRim.Events;

public enum GiftApplicationConfirmationResultKind
{
    Accepted,
    AlreadyApplied,
    EventNotFound,
    NotGiftEvent,
    NotTarget,
    NotDelivered,
    RejectedByTarget,
    SnapshotIdentityMismatch,
    SnapshotBaseMismatch,
    NotAnchored
}
