namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidTimeoutProcessingResult(
    IReadOnlyList<AuthoritativeEvent> FailedRaids,
    IReadOnlyList<AuthoritativeEvent> NotificationEvents,
    IReadOnlyList<AuthoritativeEvent> AttackerLossEvents,
    IReadOnlyList<AuthoritativeEvent> OfflineFailedRaids)
{
    public int FailedRaidCount => FailedRaids.Count;

    public int NotificationCount => NotificationEvents.Count;

    public int AttackerLossEventCount => AttackerLossEvents.Count;

    public int OfflineFailedRaidCount => OfflineFailedRaids.Count;
}
