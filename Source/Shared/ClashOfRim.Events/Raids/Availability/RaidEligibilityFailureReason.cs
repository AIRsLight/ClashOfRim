namespace AIRsLight.ClashOfRim.Events;

public enum RaidEligibilityFailureReason
{
    MissingRequest,
    MissingAttacker,
    MissingDefender,
    AttackerIsDefender,
    NotHostile,
    DefenderOnline,
    CooldownActive,
    DefenderWealthBelowMinimum,
    MissingDefenderSnapshot,
    MissingTargetMap,
    TargetMapUnavailable
}
