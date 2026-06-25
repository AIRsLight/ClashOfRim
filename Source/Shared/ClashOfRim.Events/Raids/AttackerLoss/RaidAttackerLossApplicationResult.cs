namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAttackerLossApplicationResult(
    RaidAttackerLossApplicationResultKind Kind,
    RaidAttackerLossApplicationPlan? Plan,
    IReadOnlyList<string> RemovedPawnGlobalKeys,
    IReadOnlyList<EventThingReference> RemovedThings,
    bool TriggeredVanillaCaravanLostEvent,
    bool RequiresSnapshotConfirmation,
    string? FailureReason)
{
    public bool Applied => Kind is RaidAttackerLossApplicationResultKind.AppliedWithVanillaCaravanLostEvent
        or RaidAttackerLossApplicationResultKind.AppliedWithSnapshotFallback;
}
