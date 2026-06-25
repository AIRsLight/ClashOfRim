namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidEligibilityPolicy(
    int MinimumDefenderWealth = 0,
    bool RequireHostileRelation = true,
    bool RequireDefenderOffline = true);
