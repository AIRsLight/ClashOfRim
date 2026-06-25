using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

[HarmonyPatch(typeof(Map), "get_IsPlayerHome")]
public static class RemoteSessionMapHomePatches
{
    public static bool Prefix(Map __instance, ref bool __result)
    {
        if (__instance?.Parent is RemoteSessionMapParent { IsRaidBattle: true })
        {
            // Defender maps can contain grav engines copied from the save; raid copies must still use encounter reform flow.
            __result = false;
            return false;
        }

        return true;
    }
}
