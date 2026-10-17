using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

// Raid lifecycle owns reconciliation and protection; delivery owns its admission decision.
internal interface IRaidDeliveryProtection
{
    void Reconcile(DateTimeOffset nowUtc);
    AuthoritativeEvent? FindBlockingRaid(string userId, string? colonyId);
    DateTimeOffset? CurrentCooldownUntil(string userId, string? colonyId, DateTimeOffset nowUtc);
}
