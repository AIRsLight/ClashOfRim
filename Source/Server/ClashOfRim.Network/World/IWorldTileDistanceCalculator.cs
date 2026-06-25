using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Network;

public interface IWorldTileDistanceCalculator
{
    int? TryCalculateDistance(
        WorldTileGeometryDistanceSource? geometry,
        WorldTileRef fromTile,
        WorldTileRef toTile,
        int crossLayerOverheadDistanceTiles);
}

public sealed class StraightLineWorldTileDistanceCalculator : IWorldTileDistanceCalculator
{
    public int? TryCalculateDistance(
        WorldTileGeometryDistanceSource? geometry,
        WorldTileRef fromTile,
        WorldTileRef toTile,
        int crossLayerOverheadDistanceTiles)
    {
        if (geometry is null || fromTile.Tile < 0 || toTile.Tile < 0)
        {
            return null;
        }

        if (!geometry.Layers.TryGetValue(fromTile.LayerId, out WorldTileLayerGeometry? fromLayer)
            || !geometry.Layers.TryGetValue(toTile.LayerId, out WorldTileLayerGeometry? toLayer)
            || fromLayer.AverageTileSize <= 0f
            || toLayer.AverageTileSize <= 0f)
        {
            return null;
        }

        if (!fromLayer.TileCenters.TryGetValue(fromTile.Tile, out WorldTileCenter from)
            || !toLayer.TileCenters.TryGetValue(toTile.Tile, out WorldTileCenter to))
        {
            return null;
        }

        double? horizontalDistance = CalculateHorizontalDistanceTiles(fromLayer, toLayer, from, to);
        if (horizontalDistance is null)
        {
            return null;
        }

        double distanceTiles = fromTile.LayerId == toTile.LayerId
            ? horizontalDistance.Value
            : horizontalDistance.Value + Math.Max(0, crossLayerOverheadDistanceTiles);
        if (double.IsNaN(distanceTiles) || double.IsInfinity(distanceTiles))
        {
            return null;
        }

        return Math.Max(0, (int)Math.Ceiling(distanceTiles));
    }

    private static double? CalculateHorizontalDistanceTiles(
        WorldTileLayerGeometry fromLayer,
        WorldTileLayerGeometry toLayer,
        WorldTileCenter from,
        WorldTileCenter to)
    {
        double fromLength = Math.Sqrt(from.X * from.X + from.Y * from.Y + from.Z * from.Z);
        double toLength = Math.Sqrt(to.X * to.X + to.Y * to.Y + to.Z * to.Z);
        if (fromLength <= 0d || toLength <= 0d)
        {
            return null;
        }

        double dot = (from.X / fromLength * (to.X / toLength))
            + (from.Y / fromLength * (to.Y / toLength))
            + (from.Z / fromLength * (to.Z / toLength));
        dot = Math.Clamp(dot, -1d, 1d);
        double sphericalDistance = Math.Acos(dot);
        double radius = fromLayer.LayerId == toLayer.LayerId
            ? (fromLength + toLength) / 2d
            : Math.Min(fromLength, toLength);
        double averageTileSize = fromLayer.LayerId == toLayer.LayerId
            ? fromLayer.AverageTileSize
            : Math.Min(fromLayer.AverageTileSize, toLayer.AverageTileSize);
        double distanceTiles = sphericalDistance * radius / averageTileSize;
        if (double.IsNaN(distanceTiles) || double.IsInfinity(distanceTiles))
        {
            return null;
        }

        return Math.Max(0d, distanceTiles);
    }
}

public sealed class WorldTileGeometryDistanceSource
{
    public WorldTileGeometryDistanceSource(IEnumerable<WorldTileLayerGeometry> layers)
    {
        Layers = layers
            .GroupBy(layer => layer.LayerId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public IReadOnlyDictionary<int, WorldTileLayerGeometry> Layers { get; }
}

public sealed class WorldTileLayerGeometry
{
    public WorldTileLayerGeometry(int layerId, float averageTileSize, IEnumerable<WorldTileCenter> tileCenters)
    {
        LayerId = layerId;
        AverageTileSize = averageTileSize;
        TileCenters = tileCenters
            .GroupBy(center => center.Tile)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public int LayerId { get; }

    public float AverageTileSize { get; }

    public IReadOnlyDictionary<int, WorldTileCenter> TileCenters { get; }
}

public readonly record struct WorldTileCenter(int Tile, float X, float Y, float Z);

public readonly record struct WorldTileRef(int Tile, int LayerId);
