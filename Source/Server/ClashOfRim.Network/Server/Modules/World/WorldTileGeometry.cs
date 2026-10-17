using System.Globalization;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldTileGeometry
{
    internal static bool TryParsePlanetTile(string? tileText, out WorldTileRef tileRef)
    {
        tileRef = default;
        if (string.IsNullOrWhiteSpace(tileText))
        {
            return false;
        }

        string text = tileText!.Trim();
        string[] parts = text.Split(',', 2);
        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tile)
            || tile < 0)
        {
            return false;
        }

        int layerId = 0;
        if (parts.Length == 2
            && (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId)
                || layerId < 0))
        {
            return false;
        }

        tileRef = new WorldTileRef(tile, layerId);
        return true;
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
