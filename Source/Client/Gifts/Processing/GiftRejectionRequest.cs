namespace AIRsLight.ClashOfRim.Gifts;

public sealed class GiftRejectionRequest
{
    public GiftRejectionRequest(
        string eventId,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? reason)
    {
        EventId = eventId;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Reason = reason;
    }

    public string EventId { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? Reason { get; }
}
