using HarmonyLib;
using RimWorld;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(Ideo), nameof(Ideo.IdeoTick))]
public static class IdeoTickPatches
{
    public static bool Prefix(Ideo __instance)
    {
        return !RemoteIdeoTickPolicy.ShouldSkipTick(__instance);
    }
}
