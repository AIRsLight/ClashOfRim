namespace AIRsLight.ClashOfRim.Events;

public sealed record PeaceRequestEventPayload(
    string PeaceRequestId,
    string? Message,
    DateTimeOffset? ExpiresAtUtc) : LedgerEventPayload;
