using System.Collections.Generic;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

[HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetGizmos))]
internal static class PlayerColonyAppearanceGizmoPatch
{
    public static void Postfix(WorldObject __instance, ref IEnumerable<Gizmo> __result)
    {
        if (__instance is MapParent mapParent && ClientColonyGizmos.Current?.Appearance(mapParent) is { } command)
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
