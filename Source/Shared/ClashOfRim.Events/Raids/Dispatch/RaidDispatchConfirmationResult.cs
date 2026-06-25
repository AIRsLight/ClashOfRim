namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidDispatchConfirmationResult(
    RaidAvailabilitySummary Availability,
    RaidDispatchConfirmationToken? Token)
{
    public bool CanDispatch => Availability.CanRaid && Token != null;
}
