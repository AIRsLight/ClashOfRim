namespace AIRsLight.ClashOfRim.Events;

public sealed record EventLetterProjection(
    string EventId,
    ServerEventType EventType,
    ServerEventStatus EventStatus,
    EventQueueGroupKind QueueGroup,
    EventLetterKind Kind,
    EventLetterDefName LetterDef,
    string Label,
    string Text,
    string? TargetWorldObjectId,
    string? TargetMapUniqueId,
    int? TargetTile,
    IReadOnlyList<EventLetterAction> Actions,
    bool DismissalChangesLedgerState)
{
    public bool NeedsChoice => Kind == EventLetterKind.Choice;
}
