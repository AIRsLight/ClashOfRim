using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
public static class SupportPawnInspectPatches
{
    public static void Postfix(Pawn __instance, ref string __result)
    {
        ActiveSupportPawnAssignment? assignment = ClashOfRimGameComponent.FindSupportAssignment(__instance);
        if (assignment is null || assignment.PermanentSupport)
        {
            return;
        }

        string line = assignment.InspectLine(Find.TickManager?.TicksGame ?? 0);
        __result = string.IsNullOrWhiteSpace(__result)
            ? line
            : __result + "\n" + line;
    }
}
