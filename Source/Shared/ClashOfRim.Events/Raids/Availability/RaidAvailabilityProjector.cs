namespace AIRsLight.ClashOfRim.Events;

public static class RaidAvailabilityProjector
{
    public static RaidAvailabilitySummary Project(RaidAvailabilityProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Eligibility);

        RaidEligibilityResult eligibility = RaidEligibilityChecker.Check(request.Eligibility, request.Policy);
        if (eligibility.Eligible)
        {
            return RaidAvailabilitySummary.Available(
                request.Eligibility.TargetMapUniqueId,
                request.Eligibility.DefenderSnapshot?.SnapshotId);
        }

        return new RaidAvailabilitySummary(
            CanRaid: false,
            eligibility.FailureReasons,
            eligibility.CooldownUntilUtc,
            request.Eligibility.TargetMapUniqueId,
            request.Eligibility.DefenderSnapshot?.SnapshotId,
            ChooseSuggestedAction(eligibility.FailureReasons));
    }

    private static RaidAvailabilitySuggestedAction ChooseSuggestedAction(
        IReadOnlyList<RaidEligibilityFailureReason> reasons)
    {
        if (reasons.Contains(RaidEligibilityFailureReason.NotHostile))
        {
            return RaidAvailabilitySuggestedAction.DeclareWar;
        }

        if (reasons.Contains(RaidEligibilityFailureReason.DefenderOnline))
        {
            return RaidAvailabilitySuggestedAction.WaitUntilDefenderOffline;
        }

        if (reasons.Contains(RaidEligibilityFailureReason.CooldownActive))
        {
            return RaidAvailabilitySuggestedAction.WaitForCooldown;
        }

        if (reasons.Contains(RaidEligibilityFailureReason.DefenderWealthBelowMinimum))
        {
            return RaidAvailabilitySuggestedAction.WaitForTargetWealth;
        }

        if (reasons.Contains(RaidEligibilityFailureReason.MissingDefenderSnapshot))
        {
            return RaidAvailabilitySuggestedAction.WaitForSnapshot;
        }

        if (reasons.Contains(RaidEligibilityFailureReason.MissingTargetMap) ||
            reasons.Contains(RaidEligibilityFailureReason.TargetMapUnavailable))
        {
            return RaidAvailabilitySuggestedAction.ChooseAnotherMap;
        }

        return RaidAvailabilitySuggestedAction.ReviewTarget;
    }
}
