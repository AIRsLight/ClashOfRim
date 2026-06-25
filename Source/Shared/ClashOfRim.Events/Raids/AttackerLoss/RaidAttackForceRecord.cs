namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAttackForceRecord(
    string AttackerSnapshotId,
    IReadOnlyList<string> PawnGlobalKeys,
    IReadOnlyList<EventThingReference> CarriedThings);
