namespace AIRsLight.ClashOfRim.Events;

public sealed record WarDeclarationEventPayload(
    string WarDeclarationId,
    string? Reason) : LedgerEventPayload;
