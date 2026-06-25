namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAvailabilitySummary(
    bool CanRaid,
    IReadOnlyList<RaidEligibilityFailureReason> DisabledReasons,
    DateTimeOffset? CooldownUntilUtc,
    string? TargetMapUniqueId,
    string? DefenderSnapshotId,
    RaidAvailabilitySuggestedAction SuggestedAction)
{
    public static RaidAvailabilitySummary Available(string? targetMapUniqueId, string? defenderSnapshotId)
    {
        return new RaidAvailabilitySummary(
            CanRaid: true,
            Array.Empty<RaidEligibilityFailureReason>(),
            CooldownUntilUtc: null,
            targetMapUniqueId,
            defenderSnapshotId,
            RaidAvailabilitySuggestedAction.StartRaid);
    }
}
