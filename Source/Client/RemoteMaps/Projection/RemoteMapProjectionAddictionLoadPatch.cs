using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

[HarmonyPatch(typeof(Hediff_Addiction), "get_Need")]
internal static class RemoteMapProjectionAddictionLoadPatch
{
    internal static bool Prefix(Hediff_Addiction __instance, ref Need_Chemical? __result)
    {
        if ((!RemoteMapProjectionLoadScope.Active && Scribe.mode != LoadSaveMode.LoadingVars)
            || __instance.pawn?.needs is not null)
        {
            return true;
        }

        // Pawn.ExposeData restores health before needs. Standalone reference pawns can
        // therefore expose an addiction briefly before their needs tracker is assigned,
        // including normal save reloads of a previously projected battle map.
        __result = null;
        return false;
    }
}
