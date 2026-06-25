namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidDefenseLockPolicy(TimeSpan MaxRaidDuration, TimeSpan ServerTimeoutGracePeriod)
{
    public static RaidDefenseLockPolicy Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(2));

    public RaidDefenseLockPolicy(TimeSpan maxRaidDuration)
        : this(maxRaidDuration, TimeSpan.Zero)
    {
    }

    public TimeSpan ServerTimeoutDuration => MaxRaidDuration + ServerTimeoutGracePeriod;
}
