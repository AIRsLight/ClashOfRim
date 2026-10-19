using System.Text.Json;
using RimTest.Protocol;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal static class BridgeMaps
{
    internal static Map Select(JsonElement a) => a.OptionalString("mapId") is { } id
        ? Find.Maps.Single(m => "Map_" + m.uniqueID == id) : Find.Maps.Single(m => m.IsPlayerHome);
    internal static PlanetTile NearbySettlement(Map home)
    {
        var visited = new HashSet<PlanetTile> { home.Tile }; var frontier = new List<PlanetTile> { home.Tile };
        for (int distance = 1; distance <= 8; distance++)
        {
            var next = new List<PlanetTile>();
            foreach (var origin in frontier)
            {
                var neighbors = new List<PlanetTile>(); Find.WorldGrid.GetTileNeighbors(origin, neighbors);
                foreach (var tile in neighbors)
                    if (visited.Add(tile))
                    {
                        next.Add(tile);
                        if (distance >= 4 && TileFinder.IsValidTileForNewSettlement(tile) && !Find.WorldObjects.AnyWorldObjectAt(tile)) return tile;
                    }
            }
            frontier = next;
        }
        throw new InvalidOperationException("No nearby valid relocation tile.");
    }
}
