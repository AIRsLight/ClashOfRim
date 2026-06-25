namespace AIRsLight.ClashOfRim.Raids;

public enum RaidAttackerLossApplicationResultKind
{
    AppliedWithVanillaCaravanLostEvent,
    AppliedWithSnapshotFallback,
    MissingRequest,
    SnapshotMismatch
}
