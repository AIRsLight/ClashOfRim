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
        return ClientWorldGenerationPage.Generation?.TryStart(new ClientWorldGenerationPage(__instance)) != true;
    }
}
