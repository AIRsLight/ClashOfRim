namespace AIRsLight.ClashOfRim.Events;

public enum EventApplicationResultKind
{
    None,
    ReadyToApply,
    RequiresClientConfirmation,
    SnapshotBaseMismatch,
    TargetObjectMissing,
    LossNotReflected,
    NeedsManualReview,
    Applied,
    Rejected
}
