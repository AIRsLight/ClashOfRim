using RimWorld.Planet;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal static class ClientWorldTileGeometry
{
    internal static bool Contains(World? world, PlanetTile tile) => tile.tileId >= 0 && world?.grid?.PlanetLayers is { } layers
        && layers.TryGetValue(ClientWorldRuntimeValues.ReadPlanetTileLayerId(tile), out var layer) && tile.tileId < layer.TilesCount;
}
