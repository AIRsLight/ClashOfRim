namespace AIRsLight.ClashOfRim.Events;

public sealed record AuthoritativeEvent(
    string EventId,
    ServerEventType Type,
    EventParty Actor,
    EventParty Target,
    DateTimeOffset CreatedAtUtc,
    ServerEventStatus Status,
    ServerEventDeliveryMode DeliveryMode,
    string IdempotencyKey,
    LedgerEventPayload Payload,
    EventTargetContext? TargetContext = null,
    EventRejectionPolicy RejectionPolicy = EventRejectionPolicy.NotRejectable,
    TargetEventDecision TargetDecision = TargetEventDecision.None,
    DateTimeOffset? DecisionAtUtc = null,
    string? DecisionReason = null,
    EventApplicationResultKind LastApplicationResult = EventApplicationResultKind.None,
    string? LastFailureReason = null,
    DateTimeOffset? NextRetryAtUtc = null,
    string? DeliveredToSnapshotId = null,
    DateTimeOffset? DeliveredAtUtc = null,
    string? AppliedSnapshotId = null,
    DateTimeOffset? AppliedAtUtc = null);
