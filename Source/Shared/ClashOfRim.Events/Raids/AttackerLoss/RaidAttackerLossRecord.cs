namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAttackerLossRecord(
    string SourceRaidEventId,
    string AttackerSnapshotId,
    IReadOnlyList<string> LostPawnGlobalKeys,
    IReadOnlyList<EventThingReference> LostThings,
    string Reason,
    RaidAttackerLossClientEffect ClientEffect = RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent)
{
    public static RaidAttackerLossRecord FromAttackForce(
        string sourceRaidEventId,
        RaidAttackForceRecord attackForce,
        string reason,
        RaidAttackerLossClientEffect clientEffect = RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRaidEventId);
        ArgumentNullException.ThrowIfNull(attackForce);

        return new RaidAttackerLossRecord(
            sourceRaidEventId,
            attackForce.AttackerSnapshotId,
            attackForce.PawnGlobalKeys,
            attackForce.CarriedThings,
            reason,
            clientEffect);
    }
}
