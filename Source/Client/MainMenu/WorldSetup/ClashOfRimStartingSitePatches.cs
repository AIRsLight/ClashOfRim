using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(TileFinder), nameof(TileFinder.IsValidTileForNewSettlement))]
public static class ClashOfRimStartingSiteValidationPatch
{
    public static void Postfix(PlanetTile tile, StringBuilder reason, ref bool __result)
    {
        if (!__result)
        {
            return;
        }

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (!mod.IsBlockedByServerColonySite(tile, out ModPlayerColonySiteDto? blockingSite) || blockingSite is null)
        {
            return;
        }

        __result = false;
        reason?.AppendLine(ClashOfRimText.Key(
            "ClashOfRim.StartingSiteBlockedByServerColony",
            ClashOfRimMod.FormatBlockedColonySite(blockingSite).Named("SITE")));
    }
}
