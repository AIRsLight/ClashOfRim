namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidCooldownStatus(
    string DefenderUserId,
    string? DefenderColonyId,
    IReadOnlyList<RaidCooldownRecord> Records)
{
    public DateTimeOffset? CooldownUntilUtc => Records.Count == 0
        ? null
        : Records.Max(record => record.CooldownUntilUtc);
}
