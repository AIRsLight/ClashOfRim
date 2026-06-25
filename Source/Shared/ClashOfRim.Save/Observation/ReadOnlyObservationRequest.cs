namespace AIRsLight.ClashOfRim.Save;

public sealed record ReadOnlyObservationRequest(
    string ObserverUserId,
    SnapshotIdentity TargetSnapshot,
    ObservationMapContext TargetMap);
