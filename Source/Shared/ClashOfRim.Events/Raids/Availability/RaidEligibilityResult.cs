namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidEligibilityResult(
    IReadOnlyList<RaidEligibilityFailureReason> FailureReasons,
    DateTimeOffset? CooldownUntilUtc = null)
{
    public bool Eligible => FailureReasons.Count == 0;
}
