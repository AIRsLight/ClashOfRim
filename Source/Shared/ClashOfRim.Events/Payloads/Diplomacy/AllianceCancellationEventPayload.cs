namespace AIRsLight.ClashOfRim.Events;

public sealed record AllianceCancellationEventPayload(
    string AllianceCancellationId,
    string? Message) : LedgerEventPayload;
