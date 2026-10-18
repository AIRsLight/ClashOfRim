using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

// The gift-facing view composes the same raid lifecycle, query and protection services.
internal sealed class RaidDeliveryProtection(RaidTimeouts timeouts, RaidQueries queries, RaidProtection protection)
    : IRaidDeliveryProtection
{
    public void Reconcile(DateTimeOffset nowUtc) => timeouts.Reconcile(nowUtc);
    public AuthoritativeEvent? FindBlockingRaid(string userId, string? colonyId) => queries.BlockingDefender(userId, colonyId);
    public DateTimeOffset? CurrentCooldownUntil(string userId, string? colonyId, DateTimeOffset nowUtc) =>
        protection.CurrentUntil(userId, colonyId, nowUtc);
}
