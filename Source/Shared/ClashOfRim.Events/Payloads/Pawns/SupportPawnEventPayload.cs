namespace AIRsLight.ClashOfRim.Events;

public sealed record SupportPawnEventPayload(
    string PawnGlobalKey,
    string SourceSnapshotId,
    string? PawnName,
    bool TemporaryControl,
    DateTimeOffset? ExpectedReturnAtUtc,
    CrossMapPawnReference? PawnReference = null,
    PawnExchangePackage? PawnPackage = null,
    int? SourceTile = null,
    string? SourceCaravanLoadId = null,
    bool ReturnToSender = false,
    string? RejectionReason = null,
    bool PermanentSupport = false,
    int? SupportDurationDays = null,
    long? ExpiresAtGameTicks = null,
    bool AutoReturnOnSettlement = false,
    string? SourceEventId = null,
    string? ReturnReason = null) : LedgerEventPayload;
