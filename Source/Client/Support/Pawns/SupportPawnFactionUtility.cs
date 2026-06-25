using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Diplomacy;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal static class SupportPawnFactionUtility
{
    public static Faction? ResolveOriginalFaction(ActiveSupportPawnAssignment assignment)
    {
        Faction? proxy = PlayerFactionProxyUtility.FindProxyForUser(assignment.OwnerUserId);
        if (proxy is not null)
        {
            return proxy;
        }

        IEnumerable<Faction> factions = Find.World?.factionManager?.AllFactions ?? Enumerable.Empty<Faction>();
        if (!string.IsNullOrWhiteSpace(assignment.OriginalFactionName))
        {
            Faction? named = factions.FirstOrDefault(faction =>
                !ReferenceEquals(faction, Faction.OfPlayer)
                && (string.Equals(faction.Name, assignment.OriginalFactionName, StringComparison.Ordinal)
                    || (PlayerFactionProxyUtility.IsServerPlayerProxy(faction)
                        && string.Equals(
                            PlayerFactionProxyUtility.ProxyOwnerUserId(faction),
                            assignment.OriginalFactionName,
                            StringComparison.Ordinal))));
            if (named is not null)
            {
                return named;
            }
        }

        return PlayerFactionProxyUtility.EnsureProxyForUser(
            assignment.OwnerUserId,
            assignment.OriginalFactionDefName);
    }
}
