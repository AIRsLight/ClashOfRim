namespace AIRsLight.ClashOfRim.Events;

public sealed record AllianceRequestEventPayload(
    string AllianceRequestId,
    string? Message,
    DateTimeOffset? ExpiresAtUtc) : LedgerEventPayload;
