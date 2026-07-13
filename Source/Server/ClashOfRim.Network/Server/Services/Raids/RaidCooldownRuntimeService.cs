using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static class RaidCooldownRuntimeService
{
    public static DateTimeOffset? CurrentUntil(
        ClashOfRimNetworkState state,
        string defenderUserId,
        string defenderColonyId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        IReadOnlyList<RaidCooldownRecord> projected = Project(state, defenderUserId, defenderColonyId);
        return state.RaidCooldownOverrides.ResolveCurrentUntil(
            defenderUserId,
            defenderColonyId,
            nowUtc,
            projected);
    }

    public static RaidCooldownOverrideRecord SetCurrent(
        ClashOfRimNetworkState state,
        string defenderUserId,
        string defenderColonyId,
        double hours,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defenderColonyId);
        if (!double.IsFinite(hours) || hours < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), "Cooldown hours must be a finite non-negative number.");
        }

        TimeSpan duration;
        try
        {
            duration = TimeSpan.FromHours(hours);
        }
        catch (OverflowException ex)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), hours, ex.Message);
        }

        DateTimeOffset cooldownUntilUtc;
        try
        {
            cooldownUntilUtc = nowUtc + duration;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), hours, ex.Message);
        }

        IReadOnlyList<RaidCooldownRecord> projected = Project(state, defenderUserId, defenderColonyId);
        string[] activeRaidEventIds = projected
            .Where(record => record.CooldownUntilUtc > nowUtc)
            .Select(record => record.RaidEventId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return state.RaidCooldownOverrides.SetCurrent(
            defenderUserId,
            defenderColonyId,
            nowUtc,
            cooldownUntilUtc,
            activeRaidEventIds);
    }

    private static IReadOnlyList<RaidCooldownRecord> Project(
        ClashOfRimNetworkState state,
        string defenderUserId,
        string defenderColonyId)
    {
        RaidCooldownStatus cooldown = RaidCooldownProjector.BuildForDefender(
            defenderUserId,
            defenderColonyId,
            state.Ledger.ListByTypeForTarget(ServerEventType.Raid, defenderUserId, defenderColonyId),
            policy: new RaidCooldownPolicy(
                state.ServerConfiguration.RaidProtectionDuration,
                RaidCooldownPolicy.Default.TimeoutCooldown,
                RaidCooldownPolicy.Default.CancelledCooldown),
            defenderProtectionStartResolver: raid => state.RaidProtectionActivations.FindActivatedAt(
                raid.EventId,
                raid.Target.UserId,
                raid.Target.ColonyId),
            requireDefenderProtectionActivation: true);
        return cooldown.Records;
    }
}
