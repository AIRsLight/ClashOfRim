namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidInitiationResult(
    RaidInitiationResultKind Kind,
    RaidEligibilityResult Eligibility,
    AuthoritativeEvent? RaidEvent,
    AuthoritativeEvent? NotificationEvent,
    bool Created)
{
    public bool Started => Kind is RaidInitiationResultKind.RaidEventCreated or RaidInitiationResultKind.RaidEventAlreadyExists;
}
