namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidDispatchConfirmationToken(
    string TokenId,
    string AttackerUserId,
    string DefenderUserId,
    string? DefenderColonyId,
    string? TargetMapUniqueId,
    string? DefenderSnapshotId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
