using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch]
public static class ClashOfRimNewSiteTileReservationPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(TileFinder))
            .Where(method => method.Name == nameof(TileFinder.TryFindNewSiteTile));
    }

    public static void Prefix(ref Predicate<PlanetTile> validator)
    {
        validator = ClashOfRimWorldTileReservationUtility.ExcludeServerColonySites(validator);
    }
}

[HarmonyPatch(typeof(TileFinder), nameof(TileFinder.TryFindPassableTileWithTraversalDistance))]
public static class ClashOfRimTraversalTileReservationPatch
{
    public static void Prefix(ref Predicate<PlanetTile> validator)
    {
        validator = ClashOfRimWorldTileReservationUtility.ExcludeServerColonySites(validator);
    }
}

internal static class ClashOfRimWorldTileReservationUtility
{
    public static Predicate<PlanetTile> ExcludeServerColonySites(Predicate<PlanetTile>? validator)
    {
        return tile => (validator?.Invoke(tile) ?? true) && !IsReservedByServerColony(tile);
    }

    private static bool IsReservedByServerColony(PlanetTile tile)
    {
        try
        {
            ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
            return mod.IsBlockedByServerColonySite(tile, out ModPlayerColonySiteDto? _);
        }
        catch
        {
            return false;
        }
    }
}
