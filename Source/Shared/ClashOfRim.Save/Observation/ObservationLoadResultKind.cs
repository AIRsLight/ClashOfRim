namespace AIRsLight.ClashOfRim.Save;

public enum ObservationLoadResultKind
{
    Granted,
    MissingObserver,
    MissingTargetSnapshotIdentity,
    SnapshotNotFound,
    MapNotFound,
    MapContextMismatch
}
