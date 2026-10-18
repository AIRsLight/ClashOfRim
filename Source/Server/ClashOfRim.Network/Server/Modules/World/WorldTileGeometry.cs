using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldTileGeometry
{
    internal static bool TryParsePlanetTile(string? tileText, out WorldTileRef tileRef)
    {
        bool valid = WorldTileText.TryParse(tileText, out int tile, out int layer);
        tileRef = valid ? new WorldTileRef(tile, layer) : default;
        return valid;
    }

    internal static WorldTileGeometryDistanceSource? BuildWorldTileGeometryDistanceSource(WorldConfigurationDto? configuration)
    {
        WorldTileGeometryDto? geometry = configuration?.TileGeometry;
        if (geometry is null || geometry.Layers.Count == 0)
        {
            return null;
        }

        return new WorldTileGeometryDistanceSource(
            geometry.Layers
                .Where(layer => layer.AverageTileSize > 0f && layer.TileCenters.Count > 0)
                .Select(layer => new WorldTileLayerGeometry(
                    layer.LayerId,
                    layer.AverageTileSize,
                    layer.TileCenters.Select(center => new WorldTileCenter(
                        center.Tile,
                        center.X,
                        center.Y,
                        center.Z)))));
    }

}
