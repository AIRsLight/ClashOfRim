using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

[HarmonyPatch(typeof(QuestUtility), nameof(QuestUtility.GetExtraFactionsFromQuestParts))]
public static class SupportPawnExtraFactionPatches
{
    public static void Postfix(Pawn pawn, List<ExtraFaction> outExtraFactions)
    {
        ActiveSupportPawnAssignment? assignment = ClashOfRimGameComponent.FindSupportAssignment(pawn);
        if (assignment is null || assignment.PermanentSupport)
        {
            return;
        }

        Faction? faction = SupportPawnFactionUtility.ResolveOriginalFaction(assignment);
        if (faction is null || faction == pawn.Faction)
        {
            return;
        }

        if (outExtraFactions.Any(existing =>
                existing.faction == faction && existing.factionType == ExtraFactionType.HomeFaction))
        {
            return;
        }

        outExtraFactions.Add(new ExtraFaction(faction, ExtraFactionType.HomeFaction));
    }
}
