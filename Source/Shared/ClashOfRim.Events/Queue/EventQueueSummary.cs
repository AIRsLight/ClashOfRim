namespace AIRsLight.ClashOfRim.Events;

public sealed record EventQueueSummary(
    string UserId,
    IReadOnlyList<EventQueueItem> DirectlyProcessable,
    IReadOnlyList<EventQueueItem> WaitingForConfirmation,
    IReadOnlyList<EventQueueItem> DeliveredUnconfirmed,
    IReadOnlyList<EventQueueItem> Conflicts,
    IReadOnlyList<EventQueueItem> Rejected,
    IReadOnlyList<EventQueueItem> AllItems);
