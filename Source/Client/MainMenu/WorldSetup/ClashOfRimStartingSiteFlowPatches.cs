using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(Page_SelectStartingSite), nameof(Page_SelectStartingSite.PreOpen))]
public static class ClashOfRimStartingSitePreOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix(Page_SelectStartingSite __instance)
    {
        ClientWorldSetupDiagnostics.Log(
            "Page_SelectStartingSite.PreOpen",
            ClientWorldSetupDiagnostics.DescribeWindow(__instance) + ", " + ClientWorldSetupDiagnostics.DescribeWindowStack());
    }
}

[HarmonyPatch(typeof(Page_SelectStartingSite), nameof(Page_SelectStartingSite.PostOpen))]
public static class ClashOfRimStartingSitePostOpenPatch
{
    private const float WorldMapMarkerRefreshIntervalSeconds = 30f;
    private static float nextRefreshAt;

    [HarmonyPostfix]
    public static void Postfix(Page_SelectStartingSite __instance)
    {
        ClientWorldSetupDiagnostics.Log(
            "Page_SelectStartingSite.PostOpen",
            ClientWorldSetupDiagnostics.DescribeWindow(__instance) + ", " + ClientWorldSetupDiagnostics.DescribeWindowStack());
        LoadedModManager.GetMod<ClashOfRimMod>().SubmitInitialWorldConfigurationIfPending();
        nextRefreshAt = Time.realtimeSinceStartup + WorldMapMarkerRefreshIntervalSeconds;
        LoadedModManager.GetMod<ClashOfRimMod>().RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.WorldMap.ReasonStartingSiteEntered"));
    }

    internal static void TryRefreshWhileOpen()
    {
        if (Time.realtimeSinceStartup < nextRefreshAt)
        {
            return;
        }

        nextRefreshAt = Time.realtimeSinceStartup + WorldMapMarkerRefreshIntervalSeconds;
        LoadedModManager.GetMod<ClashOfRimMod>().RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.WorldMap.ReasonStartingSiteStayed"));
    }
}

[HarmonyPatch(typeof(Page_SelectStartingSite), nameof(Page_SelectStartingSite.ExtraOnGUI))]
public static class ClashOfRimStartingSiteExtraOnGuiPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        ClashOfRimMainMenuPatches.RunQueuedMainThreadActions();
        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        mod.ApplyCachedWorldMapMarkersToWorldObjects();
        ClashOfRimStartingSitePostOpenPatch.TryRefreshWhileOpen();
    }
}

[HarmonyPatch(typeof(Page_SelectStartingSite), nameof(Page_SelectStartingSite.PostClose))]
public static class ClashOfRimStartingSitePostClosePatch
{
    [HarmonyPostfix]
    public static void Postfix(Page_SelectStartingSite __instance)
    {
        ClientWorldSetupDiagnostics.Log(
            "Page_SelectStartingSite.PostClose",
            ClientWorldSetupDiagnostics.DescribeWindow(__instance) + ", " + ClientWorldSetupDiagnostics.DescribeWindowStack());
    }
}
