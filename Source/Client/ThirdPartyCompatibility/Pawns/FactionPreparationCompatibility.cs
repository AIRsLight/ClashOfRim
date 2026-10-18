using AIRsLight.ClashOfRim.Modules.Compatibility;
using RimWorld;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;

internal sealed class FactionPreparationCompatibility
{
    private readonly List<FactionPreparedHandler> FactionPreparedHandlers = new();
    private readonly List<PlayerProxyFactionPreparedHandler> PlayerProxyFactionPreparedHandlers = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal FactionPreparationCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterFactionPreparedHandler(FactionPreparedHandler handler)
    {
        Registrations.RegisterUnique(FactionPreparedHandlers, handler);
    }

    internal void NotifyFactionPrepared(Faction faction, string purpose, string? ownerUserId = null)
    {
        if (faction is null)
        {
            return;
        }

        string normalizedPurpose = string.IsNullOrWhiteSpace(purpose) ? "Unknown" : purpose.Trim();
        foreach (FactionPreparedHandler handler in FactionPreparedHandlers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(NotifyFactionPrepared),
                handler,
                () => handler(faction, normalizedPurpose, ownerUserId));
        }
    }

    internal void RegisterPlayerProxyFactionPreparedHandler(PlayerProxyFactionPreparedHandler handler)
    {
        Registrations.RegisterUnique(PlayerProxyFactionPreparedHandlers, handler);
    }

    internal void NotifyPlayerProxyFactionPrepared(Faction faction, string ownerUserId)
    {
        if (faction is null || string.IsNullOrWhiteSpace(ownerUserId))
        {
            return;
        }

        NotifyFactionPrepared(faction, "PlayerProxy", ownerUserId);
        foreach (PlayerProxyFactionPreparedHandler handler in PlayerProxyFactionPreparedHandlers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(NotifyPlayerProxyFactionPrepared),
                handler,
                () => handler(faction, ownerUserId));
        }
    }
}
