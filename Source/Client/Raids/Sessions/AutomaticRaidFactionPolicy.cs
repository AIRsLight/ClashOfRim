using System;
using System.Collections.Generic;
using RimWorld;

namespace AIRsLight.ClashOfRim.Raids;

public static class AutomaticRaidFactionPolicy
{
    private static readonly HashSet<string> ServerPlayerFactionIds = new(StringComparer.Ordinal);

    public static void RegisterServerPlayerFactionId(string uniqueLoadId)
    {
        if (!string.IsNullOrWhiteSpace(uniqueLoadId))
        {
            ServerPlayerFactionIds.Add(uniqueLoadId);
        }
    }

    public static bool IsRegisteredServerPlayerFactionId(string? uniqueLoadId)
    {
        return uniqueLoadId is not null
            && !string.IsNullOrWhiteSpace(uniqueLoadId)
            && ServerPlayerFactionIds.Contains(uniqueLoadId);
    }

    public static bool IsBlockedForAutomaticNpcRaid(Faction? faction)
    {
        if (faction == null)
        {
            return false;
        }

        if (faction == Faction.OfPlayer || faction.IsPlayer)
        {
            return true;
        }

        if (faction.temporary && IsLikelyClashOfRimPlayerProxy(faction))
        {
            return true;
        }

        string uniqueLoadId = faction.GetUniqueLoadID();
        return IsRegisteredServerPlayerFactionId(uniqueLoadId);
    }

    private static bool IsLikelyClashOfRimPlayerProxy(Faction faction)
    {
        string defName = faction.def?.defName ?? string.Empty;
        string name = faction.Name ?? string.Empty;

        return defName.StartsWith("ClashOfRim_", StringComparison.Ordinal)
            || name.StartsWith("ClashOfRim_", StringComparison.Ordinal)
            || IsRegisteredServerPlayerFactionId(faction.GetUniqueLoadID());
    }
}
