namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidDefenseLock(
    string RaidEventId,
    string DefenderUserId,
    string? DefenderColonyId,
    string? TargetMapUniqueId,
    string DefenderSnapshotId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LockedUntilUtc)
{
    public TimeSpan RemainingAt(DateTimeOffset nowUtc)
    {
        TimeSpan remaining = LockedUntilUtc - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
