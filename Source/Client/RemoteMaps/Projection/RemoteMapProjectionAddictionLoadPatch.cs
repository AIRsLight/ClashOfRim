using HarmonyLib;
using RimWorld;

namespace AIRsLight.ClashOfRim.RemoteMaps;

[HarmonyPatch(typeof(Hediff_Addiction), "get_Need")]
internal static class RemoteMapProjectionAddictionLoadPatch
{
    internal static bool Prefix(Hediff_Addiction __instance, ref Need_Chemical? __result)
    {
        if (!RemoteMapProjectionLoadScope.Active || __instance.pawn?.needs is not null)
        {
            return true;
        }

        // Pawn.ExposeData restores health before needs. Standalone reference pawns can
        // therefore expose an addiction briefly before their needs tracker is assigned.
        __result = null;
        return false;
    }
}
