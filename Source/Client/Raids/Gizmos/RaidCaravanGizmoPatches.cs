using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class RaidCaravanGizmoPatches
{
    public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
    {
        if (__instance == null || __instance.Destroyed || !__instance.Spawned || !__instance.IsPlayerControlled)
        {
            return;
        }

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        IReadOnlyList<ModWorldMapMarkerDto> targets = mod.FindRaidableTargetsForCaravan(__instance);
        if (targets.Count == 0)
        {
            return;
        }

        bool hasOwnerPawn = false;
        foreach (Pawn pawn in __instance.PawnsListForReading)
        {
            if (pawn != null && !pawn.Dead && __instance.IsOwner(pawn))
            {
                hasOwnerPawn = true;
                break;
            }
        }

        if (!hasOwnerPawn)
        {
            return;
        }

        var command = new Command_Action
        {
            defaultLabel = targets.Count == 1
                ? ClashOfRimText.Key("ClashOfRim.Raid.CaravanGizmo")
                : ClashOfRimText.Key("ClashOfRim.Raid.CaravanGizmoCount", targets.Count.Named("COUNT")),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.Raid.CaravanGizmoDesc"),
            icon = ClashCommandIcons.LaunchRaid,
            action = () => mod.OpenCaravanRaidMenu(__instance, targets)
        };

        __result = AppendGizmo(__result, command);
    }

    private static IEnumerable<Gizmo> AppendGizmo(IEnumerable<Gizmo> source, Gizmo appended)
    {
        foreach (Gizmo gizmo in source)
        {
            yield return gizmo;
        }

        yield return appended;
    }
}
