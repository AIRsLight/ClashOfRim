namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidInitiationRequest(
    string IdempotencyKey,
    RaidEligibilityRequest Eligibility,
    EventTargetContext TargetContext,
    bool AttackerOnline,
    DateTimeOffset CreatedAtUtc,
    RaidAttackForceRecord? AttackForce = null);
