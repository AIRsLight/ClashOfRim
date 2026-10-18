using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.Modules.World.WorldTileGeometry;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class ColonySiteRules
{
    internal static IReadOnlyList<PlayerColonySiteDto> NormalizePlayerColonySites(
        string userId,
        string colonyId,
        IEnumerable<PlayerColonySiteDto> sites)
    {
        var bySite = new Dictionary<(string UserId, string ColonyId, string SiteId), PlayerColonySiteDto>();
        foreach (PlayerColonySiteDto site in sites)
        {
            if (site.Tile < 0)
            {
                continue;
            }

            PlayerColonySiteDto normalized = new PlayerColonySiteDto(
                userId,
                colonyId,
                site.WorldObjectId,
                site.MapUniqueId,
                site.Tile,
                site.Label,
                site.FactionName,
                site.Appearance,
                site.TileLayerId);
            bySite[WorldColonySites.Key(normalized)] = normalized;
        }

        return bySite.Values
            .OrderBy(site => site.Tile)
            .ThenBy(site => site.MapUniqueId, StringComparer.Ordinal)
            .ToList();
    }

    internal static PlayerColonySiteDto? FindSameTilePlayerColonyConflict(
        IEnumerable<PlayerColonySiteDto> existingSites,
        IEnumerable<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        HashSet<(int Tile, int LayerId)> requestedTiles = requestedSites
            .Select(site => (site.Tile, Math.Max(0, site.TileLayerId)))
            .ToHashSet();
        return existingSites.FirstOrDefault(site =>
            requestedTiles.Contains((site.Tile, Math.Max(0, site.TileLayerId)))
            && (!string.Equals(site.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)));
    }

    internal static PlayerColonySiteDto? FindNearbyPlayerColonyConflict(
        WorldConfigurationDto? configuration,
        IWorldTileDistanceCalculator distances,
        IEnumerable<PlayerColonySiteDto> existingSites,
        IEnumerable<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        WorldTileGeometryDistanceSource? geometry = BuildWorldTileGeometryDistanceSource(configuration);
        if (geometry is null)
        {
            return null;
        }

        foreach (PlayerColonySiteDto requested in requestedSites.Where(site => site.Tile >= 0))
        {
            foreach (PlayerColonySiteDto existing in existingSites.Where(site => site.Tile >= 0))
            {
                if (string.Equals(existing.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(existing.ColonyId, colonyId, StringComparison.Ordinal))
                {
                    continue;
                }

                int? distance = distances.TryCalculateDistance(
                    geometry,
                    new WorldTileRef(requested.Tile, Math.Max(0, requested.TileLayerId)),
                    new WorldTileRef(existing.Tile, Math.Max(0, existing.TileLayerId)),
                    crossLayerOverheadDistanceTiles: 0);
                if (distance is 1)
                {
                    return existing;
                }
            }
        }

        return null;
    }

    internal static string? FindExistingUserColonyConflict(
        IEnumerable<PlayerColonySiteDto> existingSites,
        IReadOnlyList<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        PlayerColonySiteDto? requested = requestedSites.FirstOrDefault();
        if (requested is null)
        {
            return null;
        }

        List<PlayerColonySiteDto> userSites = existingSites
            .Where(site => string.Equals(site.UserId, userId, StringComparison.Ordinal) && site.Tile >= 0)
            .GroupBy(WorldColonySites.Key)
            .Select(group => group.First())
            .ToList();
        if (userSites.Count == 0)
        {
            return null;
        }

        PlayerColonySiteDto? otherColony = userSites.FirstOrDefault(site =>
            !string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal));
        if (otherColony is not null)
        {
            return ServerText.Format("WorldColonySites.PlayerAlreadyHasNamedColony", ("USER", userId), ("COLONY", otherColony.ColonyId));
        }

        return null;
    }

    internal static PlayerColonySiteDto? FindExistingSameColonySiteOnDifferentTile(
        IEnumerable<PlayerColonySiteDto> existingSites,
        IReadOnlyList<PlayerColonySiteDto> requestedSites,
        string userId,
        string colonyId)
    {
        PlayerColonySiteDto? requested = requestedSites.FirstOrDefault();
        if (requested is null || requested.Tile < 0)
        {
            return null;
        }

        return existingSites
            .Where(site => string.Equals(site.UserId, userId, StringComparison.Ordinal)
                && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)
                && site.Tile >= 0)
            .GroupBy(WorldColonySites.Key)
            .Select(group => group.First())
            .FirstOrDefault(site => site.Tile != requested.Tile
                || Math.Max(0, site.TileLayerId) != Math.Max(0, requested.TileLayerId));
    }

}
