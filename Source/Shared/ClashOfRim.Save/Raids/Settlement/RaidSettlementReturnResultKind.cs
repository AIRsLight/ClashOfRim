namespace AIRsLight.ClashOfRim.Save;

public enum RaidSettlementReturnResultKind
{
    Accepted,
    MissingRequest,
    MissingEventId,
    MissingOriginalSnapshot,
    MissingReturnedSnapshot,
    MissingOriginalSnapshotIdentity,
    OriginalSnapshotIdentityMismatch,
    ReturnedSnapshotColonyMismatch,
    MissingTargetMap,
    TargetMapNotFoundInOriginalSnapshot,
    TargetMapNotFoundInReturnedSnapshot,
    InvalidLossRatio
}
