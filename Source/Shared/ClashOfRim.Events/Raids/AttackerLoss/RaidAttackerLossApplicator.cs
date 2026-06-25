namespace AIRsLight.ClashOfRim.Events;

public static class RaidAttackerLossApplicator
{
    public static RaidAttackerLossApplicationResult Apply(
        RaidAttackerLossRecord? loss,
        RaidAttackerLossClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (loss == null)
        {
            return Rejected(RaidAttackerLossApplicationResultKind.MissingLossRecord, "Missing attacker loss record.");
        }

        RaidAttackerLossApplicationPlan plan = RaidAttackerLossApplicationPlanner.FromLoss(loss);
        if (!string.Equals(plan.AttackerSnapshotId, context.CurrentSnapshotId, StringComparison.Ordinal))
        {
            return new RaidAttackerLossApplicationResult(
                RaidAttackerLossApplicationResultKind.SnapshotMismatch,
                plan,
                Array.Empty<string>(),
                Array.Empty<EventThingReference>(),
                TriggeredVanillaCaravanLostEvent: false,
                RequiresSnapshotConfirmation: false,
                FailureReason: "The current snapshot does not match the attacker snapshot bound to this loss event.");
        }

        bool triggerVanilla = plan.ShouldTriggerVanillaCaravanLostEvent && context.MatchingCaravanFound;
        return new RaidAttackerLossApplicationResult(
            triggerVanilla
                ? RaidAttackerLossApplicationResultKind.AppliedWithVanillaCaravanLostEvent
                : RaidAttackerLossApplicationResultKind.AppliedWithSnapshotFallback,
            plan,
            plan.LostPawnGlobalKeys,
            plan.LostThings,
            TriggeredVanillaCaravanLostEvent: triggerVanilla,
            RequiresSnapshotConfirmation: true,
            FailureReason: triggerVanilla ? null : "未找到对应远行队；已改用最近确认快照应用损失。");
    }

    private static RaidAttackerLossApplicationResult Rejected(
        RaidAttackerLossApplicationResultKind kind,
        string failureReason)
    {
        return new RaidAttackerLossApplicationResult(
            kind,
            Plan: null,
            Array.Empty<string>(),
            Array.Empty<EventThingReference>(),
            TriggeredVanillaCaravanLostEvent: false,
            RequiresSnapshotConfirmation: false,
            failureReason);
    }
}
