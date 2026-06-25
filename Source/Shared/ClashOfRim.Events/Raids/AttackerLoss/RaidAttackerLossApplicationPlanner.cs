namespace AIRsLight.ClashOfRim.Events;

public static class RaidAttackerLossApplicationPlanner
{
    public static RaidAttackerLossApplicationPlan FromLoss(RaidAttackerLossRecord loss)
    {
        ArgumentNullException.ThrowIfNull(loss);

        return new RaidAttackerLossApplicationPlan(
            loss.SourceRaidEventId,
            loss.AttackerSnapshotId,
            loss.LostPawnGlobalKeys,
            loss.LostThings,
            loss.ClientEffect,
            VanillaLetterLabelKey: loss.ClientEffect == RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent
                ? "LetterLabelAllCaravanColonistsDied"
                : null,
            VanillaLetterTextKey: loss.ClientEffect == RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent
                ? "LetterAllCaravanColonistsDied"
                : null);
    }
}
