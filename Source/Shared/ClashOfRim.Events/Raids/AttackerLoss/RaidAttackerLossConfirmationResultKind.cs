namespace AIRsLight.ClashOfRim.Events;

public enum RaidAttackerLossConfirmationResultKind
{
    Accepted,
    AlreadyApplied,
    EventNotFound,
    NotAttackerLossEvent,
    SourceRaidMismatch,
    SnapshotIdentityMismatch,
    SnapshotBaseMismatch,
    LossNotReflected
}
