namespace AIRsLight.ClashOfRim.Events;

public enum RaidAttackerLossApplicationResultKind
{
    AppliedWithVanillaCaravanLostEvent,
    AppliedWithSnapshotFallback,
    MissingLossRecord,
    SnapshotMismatch
}
