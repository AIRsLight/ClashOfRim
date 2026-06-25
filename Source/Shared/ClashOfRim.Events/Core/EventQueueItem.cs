namespace AIRsLight.ClashOfRim.Events;

public sealed record EventQueueItem(
    string EventId,
    ServerEventType Type,
    ServerEventStatus Status,
    DateTimeOffset CreatedAtUtc,
    string? TargetWorldObjectId,
    string? TargetMapUniqueId,
    int? TargetTile,
    EventLandingMode LandingMode,
    string? FailureReason,
    bool NeedsUserChoice,
    bool RequiresClientApplication,
    EventQueueGroupKind Group);
