using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Raids;

namespace AIRsLight.ClashOfRim.Network;

// Removed when M7 migrates raid lifecycle. It contains no gift rules or storage implementation.
internal sealed class LegacyRaidDeliveryProtection(ClashOfRimNetworkState state) : IRaidDeliveryProtection
{
    public void Reconcile(DateTimeOffset nowUtc) => ClashOfRimNetworkServer.ReconcileExpiredRaidEvents(state, nowUtc);
    public AuthoritativeEvent? FindBlockingRaid(string userId, string? colonyId) =>
        ClashOfRimNetworkServer.FindDefenderLoginBlockingRaid(state, userId, colonyId);
    public DateTimeOffset? CurrentCooldownUntil(string userId, string? colonyId, DateTimeOffset nowUtc) =>
        string.IsNullOrWhiteSpace(colonyId) ? null : RaidCooldownRuntimeService.CurrentUntil(state, userId, colonyId, nowUtc);
}
