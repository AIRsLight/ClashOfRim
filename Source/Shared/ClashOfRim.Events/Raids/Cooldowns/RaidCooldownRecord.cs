namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidCooldownRecord(
    string RaidEventId,
    string DefenderUserId,
    string? DefenderColonyId,
    RaidCooldownReason Reason,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset CooldownUntilUtc);
