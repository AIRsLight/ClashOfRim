namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidDispatchConfirmationRequest(
    RaidEligibilityRequest Eligibility,
    DateTimeOffset RequestedAtUtc,
    TimeSpan TokenLifetime);
