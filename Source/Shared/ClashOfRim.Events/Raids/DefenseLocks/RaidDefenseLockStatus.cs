namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidDefenseLockStatus(
    string DefenderUserId,
    string? DefenderColonyId,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<RaidDefenseLock> ActiveLocks)
{
    public bool IsLocked => ActiveLocks.Count > 0;

    public TimeSpan LongestRemaining => ActiveLocks.Count == 0
        ? TimeSpan.Zero
        : ActiveLocks.Max(lockState => lockState.RemainingAt(CheckedAtUtc));
}
