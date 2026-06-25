using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.PostOpen))]
public static class ClashOfRimCreateWorldParamsPatches
{
    [HarmonyPrefix]
    public static bool Prefix(Page_CreateWorldParams __instance)
    {
        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        return !mod.TryGenerateWorldFromServerConfiguration(__instance);
    }
}
