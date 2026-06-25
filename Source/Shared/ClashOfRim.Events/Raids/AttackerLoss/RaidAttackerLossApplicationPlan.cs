namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAttackerLossApplicationPlan(
    string SourceRaidEventId,
    string AttackerSnapshotId,
    IReadOnlyList<string> LostPawnGlobalKeys,
    IReadOnlyList<EventThingReference> LostThings,
    RaidAttackerLossClientEffect ClientEffect,
    string? VanillaLetterLabelKey,
    string? VanillaLetterTextKey)
{
    public bool RequiresSnapshotConfirmation => true;

    public bool ShouldTriggerVanillaCaravanLostEvent =>
        ClientEffect == RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent;
}
