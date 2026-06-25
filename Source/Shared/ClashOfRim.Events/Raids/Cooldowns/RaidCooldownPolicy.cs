namespace AIRsLight.ClashOfRim.Events;

public sealed record RaidCooldownPolicy(
    TimeSpan SettlementCooldown,
    TimeSpan TimeoutCooldown,
    TimeSpan CancelledCooldown)
{
    public static RaidCooldownPolicy Default { get; } = new(
        SettlementCooldown: TimeSpan.FromDays(3),
        TimeoutCooldown: TimeSpan.FromDays(1),
        CancelledCooldown: TimeSpan.FromHours(12));
}
