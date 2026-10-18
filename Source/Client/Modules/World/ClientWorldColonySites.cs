using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldColonySites
{
    // Map records take precedence. A tile is valid only in its declared layer;
    // a surface tile count cannot validate a settlement on another planet layer.
    internal static List<ModPlayerColonySiteDto> Merge(IEnumerable<ModPlayerColonySiteDto> maps,
        IEnumerable<ModPlayerColonySiteDto> objects, IReadOnlyDictionary<int, int> layerSizes)
    {
        bool Valid(ModPlayerColonySiteDto site) => site.Tile >= 0 && layerSizes.TryGetValue(site.TileLayerId, out int count) && site.Tile < count;
        var sites = maps.Where(Valid).ToList();
        var ids = new HashSet<string?>(sites.Select(site => site.WorldObjectId), StringComparer.Ordinal);
        var tiles = new HashSet<(int Layer, int Tile)>(sites.Select(site => (site.TileLayerId, site.Tile)));
        foreach (var site in objects.Where(Valid))
        {
            if (ids.Contains(site.WorldObjectId) || tiles.Contains((site.TileLayerId, site.Tile))) continue;
            sites.Add(site); ids.Add(site.WorldObjectId); tiles.Add((site.TileLayerId, site.Tile));
        }
        return sites.GroupBy(site => site.MapUniqueId is { } map
                ? (Map: map, Layer: 0, Tile: 0) : (Map: (string?)null, Layer: site.TileLayerId, Tile: site.Tile))
            .Select(group => group.First()).OrderBy(site => site.Tile).ThenBy(site => site.TileLayerId).ToList();
    }
}
