using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

[HarmonyPatch(typeof(IncidentWorker_RaidEnemy), nameof(IncidentWorker_RaidEnemy.FactionCanBeGroupSource))]
public static class RaidEnemyFactionCanBeGroupSourcePatch
{
    public static void Postfix(Faction f, ref bool __result)
    {
        if (__result && AutomaticRaidFactionPolicy.IsBlockedForAutomaticNpcRaid(f))
        {
            __result = false;
        }
    }
}

[HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "TryResolveRaidFaction")]
public static class RaidEnemyTryResolveRaidFactionPatch
{
    public static void Prefix(IncidentParms parms)
    {
        if (AutomaticRaidFactionPolicy.IsBlockedForAutomaticNpcRaid(parms.faction))
        {
            Log.Warning("[ClashOfRim] Blocked preselected player faction from automatic NPC raid faction resolution: " + Describe(parms.faction));
            parms.faction = null;
        }
    }

    public static void Postfix(IncidentParms parms, ref bool __result)
    {
        if (__result && AutomaticRaidFactionPolicy.IsBlockedForAutomaticNpcRaid(parms.faction))
        {
            Log.Warning("[ClashOfRim] Automatic NPC raid resolved to a blocked player faction. Cancelling raid: " + Describe(parms.faction));
            parms.faction = null;
            __result = false;
        }
    }

    private static string Describe(Faction? faction)
    {
        if (faction == null)
        {
            return "<null>";
        }

        return faction.Name + " (" + faction.GetUniqueLoadID() + ", " + faction.def.defName + ")";
    }
}

[HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
public static class RaidTryGenerateRaidInfoPatch
{
    public static void Prefix(IncidentWorker_Raid __instance, IncidentParms parms)
    {
        if (__instance is IncidentWorker_RaidEnemy && AutomaticRaidFactionPolicy.IsBlockedForAutomaticNpcRaid(parms.faction))
        {
            Log.Warning("[ClashOfRim] Cleared blocked player faction before automatic NPC raid generation: " + parms.faction.Name);
            parms.faction = null;
        }
    }

    public static void Postfix(IncidentWorker_Raid __instance, IncidentParms parms, ref List<Pawn>? pawns, ref bool __result)
    {
        if (!__result || __instance is not IncidentWorker_RaidEnemy)
        {
            return;
        }

        if (AutomaticRaidFactionPolicy.IsBlockedForAutomaticNpcRaid(parms.faction) || ContainsBlockedRaidPawn(pawns))
        {
            Log.Warning("[ClashOfRim] Automatic NPC raid generated blocked player-faction pawns. Cancelling and cleaning generated pawns.");
            CleanupGeneratedPawns(pawns);
            pawns = null;
            parms.faction = null;
            __result = false;
        }
    }

    private static bool ContainsBlockedRaidPawn(List<Pawn>? pawns)
    {
        return pawns != null && pawns.Any(pawn => AutomaticRaidFactionPolicy.IsBlockedForAutomaticNpcRaid(pawn.Faction));
    }

    private static void CleanupGeneratedPawns(List<Pawn>? pawns)
    {
        if (pawns == null)
        {
            return;
        }

        foreach (Pawn pawn in pawns)
        {
            if (pawn != null && !pawn.Destroyed)
            {
                pawn.Destroy(DestroyMode.Vanish);
            }
        }
    }
}
