using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

// The ledger owns storage; these views distinguish source raids from the
// server-to-attacker loss receipt that must be consumed before another raid.
internal sealed class RaidQueries(IAuthoritativeEventLedger ledger, Func<ClashOfRimServerConfiguration> configuration)
{
    internal AuthoritativeEvent? UnsettledAttacker(string? userId, string? colonyId, string? excludedKey = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var candidates = ledger.ListByTypeForActor(ServerEventType.Raid, userId, colonyId)
            .Concat(ledger.ListByTypeForTarget(ServerEventType.Raid, userId, colonyId)).DistinctBy(e => e.EventId);
        return RaidUnsettledProjector.BuildForAttacker(userId, colonyId, candidates, excludedKey).FirstOrDefault();
    }

    internal AuthoritativeEvent? BlockingDefender(string? userId, string? colonyId) => string.IsNullOrWhiteSpace(userId) ? null
        : RaidUnsettledProjector.BuildForDefender(userId, colonyId,
            ledger.ListByTypeForTarget(ServerEventType.Raid, userId, colonyId)).FirstOrDefault();

    internal AuthoritativeEvent? ActiveAttacker(string? userId, string? colonyId) => string.IsNullOrWhiteSpace(userId) ? null
        : RaidUnsettledProjector.BuildActiveSourceForAttacker(userId, colonyId,
            ledger.ListByTypeForActor(ServerEventType.Raid, userId, colonyId)).FirstOrDefault();

    internal ActiveRaidRecoveryDto? Recovery(string? userId, string? colonyId, DateTimeOffset nowUtc)
    {
        var raid = ActiveAttacker(userId, colonyId);
        if (raid?.Payload is not RaidEventPayload { StartedAtUtc: { } started } payload) return null;
        var policy = RaidPolicies.BuildDefenseLock(configuration());
        return new(raid.EventId, raid.Status.ToString(), nowUtc, started,
            started + policy.MaxRaidDuration, started + policy.ServerTimeoutDuration,
            raid.Target.UserId, raid.Target.ColonyId, payload.DefenderSnapshotId);
    }
}
