using HarmonyLib;
using RimWorld;

namespace AIRsLight.ClashOfRim.Diplomacy;

[HarmonyPatch(typeof(Faction), "get_GetReportText")]
public static class PlayerFactionGetReportTextPatch
{
    public static bool Prefix(Faction __instance, ref string __result)
    {
        if (!PlayerFactionProxyUtility.IsServerPlayerProxy(__instance))
        {
            return true;
        }

        __result = PlayerFactionProxyUtility.BuildReportText(__instance);
        return false;
    }
}
