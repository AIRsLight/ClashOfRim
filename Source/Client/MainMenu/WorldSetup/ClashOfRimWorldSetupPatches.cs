using HarmonyLib;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
public static class ClashOfRimWorldSetupPatches
{
    [HarmonyPostfix]
    public static void Postfix(Game __instance)
    {
        _ = ClientWorldSetupPages.Current?.InitializedAsync(__instance);
    }
}
