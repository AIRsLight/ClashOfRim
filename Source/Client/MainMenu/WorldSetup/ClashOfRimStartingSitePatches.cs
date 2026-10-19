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

        if (ClientWorldSiteReservations.Current is not { } reservations
            || !reservations.IsBlocked(tile, out ModPlayerColonySiteDto? blockingSite) || blockingSite is null)
        {
            return;
        }

        __result = false;
        reason?.AppendLine(ClashOfRimText.Key(
            "ClashOfRim.StartingSiteBlockedByServerColony",
            ClientWorldSiteReservations.Format(blockingSite).Named("SITE")));
    }
}
