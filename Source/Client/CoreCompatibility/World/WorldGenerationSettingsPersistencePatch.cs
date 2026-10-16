using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

// Vanilla WorldInfo omits these generation settings, resetting them after load.
[HarmonyPatch(typeof(WorldInfo), nameof(WorldInfo.ExposeData))]
internal static class WorldGenerationSettingsPersistencePatch
{
    private static void Postfix(WorldInfo __instance)
    {
        Scribe_Values.Look(ref __instance.overallPopulation, "clashOfRimOverallPopulation", OverallPopulation.Normal);
        Scribe_Values.Look(ref __instance.landmarkDensity, "clashOfRimLandmarkDensity", LandmarkDensity.Normal);
    }
}
