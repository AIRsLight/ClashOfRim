using AIRsLight.ClashOfRim.Modules.World;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

[HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetGizmos))]
internal static class PlayerColonyAppearanceGizmoPatch
{
    public static void Postfix(WorldObject __instance, ref IEnumerable<Gizmo> __result)
    {
        if (__instance is not MapParent mapParent
            || !ClientColonyAppearanceTarget.CanEdit(mapParent))
        {
            return;
        }

        ClashOfRimMod mod;
        try
        {
            mod = LoadedModManager.GetMod<ClashOfRimMod>();
        }
        catch
        {
            return;
        }

        if (!mod.IsNetworkConfigured)
        {
            return;
        }

        Command_Action command = new()
        {
            defaultLabel = ClashOfRimUiUtility.SelectionLabel(
                ClashOfRimText.Key("ClashOfRim.ColonyAppearance.CommandLabel")),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.ColonyAppearance.CommandDesc"),
            icon = BuildCommandIcon(mod),
            Order = 980f,
            action = () => mod.OpenColonyAppearanceWindow(mapParent)
        };

        __result = AppendGizmo(__result, command);
    }

    private static Texture2D BuildCommandIcon(ClashOfRimMod mod)
    {
        ColonyAppearanceSelection appearance = mod.CurrentColonyAppearanceSelection();
        return RemoteColonyWorldIconCache.GetTexture(
            appearance.Mode,
            appearance.IconDefName,
            appearance.ColorDefName,
            appearance.ColorHex,
            relationKind: null);
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
