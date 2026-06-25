namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidAvailabilityProjectionRequest(
    RaidEligibilityRequest Eligibility,
    RaidEligibilityPolicy? Policy = null);
