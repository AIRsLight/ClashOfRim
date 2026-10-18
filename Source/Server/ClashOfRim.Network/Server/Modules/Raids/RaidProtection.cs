using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidProtection(IAuthoritativeEventLedger ledger, RaidProtectionActivationRegistry activations,
    RaidCooldownOverrideRegistry overrides, Func<ClashOfRimServerConfiguration> configuration, ILogger logger)
{
    internal void OnLifecycle(ClientLifecycleEvent lifecycle)
    {
        if (lifecycle.Kind != ClientLifecycleEventKind.LoggedIn || string.IsNullOrWhiteSpace(lifecycle.UserId)
            || string.IsNullOrWhiteSpace(lifecycle.ColonyId)) return;
        int activated = 0;
        // Use the same projection as admission/administration. In particular,
        // disabled durations and attacker loss receipts cannot start protection.
        foreach (var record in Project(lifecycle.UserId, lifecycle.ColonyId))
            if (activations.ActivateIfMissing(record.RaidEventId, lifecycle.UserId, lifecycle.ColonyId, lifecycle.OccurredAtUtc)) activated++;
        if (activated > 0) logger.LogInformation(
            "Activated raid protection countdown for defender user={UserId} colony={ColonyId} count={Count}",
            lifecycle.UserId, lifecycle.ColonyId, activated);
    }

    internal DateTimeOffset? CurrentUntil(string userId, string? colonyId, DateTimeOffset nowUtc) =>
        string.IsNullOrWhiteSpace(colonyId) ? null : overrides.ResolveCurrentUntil(userId, colonyId, nowUtc, Project(userId, colonyId));

    internal RaidCooldownOverrideRecord SetCurrent(string userId, string colonyId, double hours, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        if (!double.IsFinite(hours) || hours < 0d)
            throw new ArgumentOutOfRangeException(nameof(hours), "Cooldown hours must be a finite non-negative number.");
        DateTimeOffset until;
        try { until = nowUtc + TimeSpan.FromHours(hours); }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        { throw new ArgumentOutOfRangeException(nameof(hours), hours, ex.Message); }
        string[] replaced = Project(userId, colonyId).Where(record => record.CooldownUntilUtc > nowUtc)
            .Select(record => record.RaidEventId).Distinct(StringComparer.Ordinal).ToArray();
        return overrides.SetCurrent(userId, colonyId, nowUtc, until, replaced);
    }

    private IReadOnlyList<RaidCooldownRecord> Project(string userId, string colonyId) => RaidCooldownProjector.BuildForDefender(
        userId, colonyId, ledger.ListByTypeForTarget(ServerEventType.Raid, userId, colonyId),
        policy: RaidPolicies.BuildCooldown(configuration()),
        defenderProtectionStartResolver: raid => activations.FindActivatedAt(raid.EventId, raid.Target.UserId, raid.Target.ColonyId),
        requireDefenderProtectionActivation: true).Records;
}
