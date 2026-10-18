using System.Globalization;

namespace AIRsLight.ClashOfRim.Save;

public static class PlayerColonyAnchors
{
    public static IReadOnlyList<SnapshotColonyAnchor> Extract(SaveSnapshotIndex index,
        IReadOnlyList<Func<WorldObjectSummary, bool>>? additionalPredicates = null)
    {
        var anchors = new List<SnapshotColonyAnchor>();
        HashSet<string> playerFactionIds = BuildSnapshotPlayerFactionIds(index);
        foreach (MapSummary map in index.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.ParentWorldObjectId))
            {
                continue;
            }

            WorldObjectSummary? worldObject = FindSnapshotWorldObjectById(index.WorldObjects, map.ParentWorldObjectId!);
            if (worldObject is null
                || worldObject.Destroyed
                || !IsSnapshotPlayerColonyAnchor(map, worldObject, additionalPredicates, playerFactionIds)
                || !WorldTileText.TryParse(worldObject.Tile, out int tile, out int tileLayerId))
            {
                continue;
            }

            anchors.Add(new SnapshotColonyAnchor(
                map.UniqueId,
                worldObject.UniqueLoadId ?? worldObject.Id,
                tile,
                tileLayerId,
                worldObject.Name));
        }

        return anchors
            .GroupBy(
                anchor => anchor.WorldObjectId
                    ?? anchor.MapUniqueId
                    ?? "tile:" + anchor.Tile.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + anchor.TileLayerId.ToString(CultureInfo.InvariantCulture),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static HashSet<string> BuildSnapshotPlayerFactionIds(SaveSnapshotIndex index)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (FactionSummary faction in index.Factions)
        {
            if (!IsSnapshotPlayerFaction(faction))
            {
                continue;
            }

            AddSnapshotFactionId(result, faction.UniqueLoadId);
            AddSnapshotFactionId(result, faction.LoadId);
            if (!string.IsNullOrWhiteSpace(faction.LoadId))
            {
                AddSnapshotFactionId(result, "Faction_" + faction.LoadId);
            }
        }

        return result;
    }

    private static bool IsSnapshotPlayerFaction(FactionSummary faction)
    {
        return string.Equals(faction.Def, "PlayerColony", StringComparison.Ordinal)
            || string.Equals(faction.Def, "PlayerTribe", StringComparison.Ordinal);
    }

    private static void AddSnapshotFactionId(HashSet<string> result, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            result.Add(value);
        }
    }

    private static WorldObjectSummary? FindSnapshotWorldObjectById(
        IEnumerable<WorldObjectSummary> worldObjects,
        string worldObjectId)
    {
        return worldObjects.FirstOrDefault(candidate =>
            string.Equals(candidate.UniqueLoadId, worldObjectId, StringComparison.Ordinal)
            || string.Equals(candidate.Id, worldObjectId, StringComparison.Ordinal));
    }

    private static bool IsSnapshotPlayerColonyAnchor(
        MapSummary map,
        WorldObjectSummary worldObject,
        IReadOnlyList<Func<WorldObjectSummary, bool>>? additionalPredicates,
        IReadOnlySet<string> playerFactionIds)
    {
        if (WorldObjectTypeIdentity.IsSettlement(worldObject))
        {
            return HasSnapshotPlayerColonyEvidence(map, worldObject, playerFactionIds);
        }

        return map.WasSpawnedViaGravshipLanding
            && HasSnapshotPlayerColonyEvidence(map, worldObject, playerFactionIds)
            && additionalPredicates?.Any(predicate => predicate(worldObject)) == true;
    }

    private static bool HasSnapshotPlayerColonyEvidence(
        MapSummary map,
        WorldObjectSummary worldObject,
        IReadOnlySet<string> playerFactionIds)
    {
        return map.PlayerColonistCount > 0
            || (!string.IsNullOrWhiteSpace(worldObject.Faction)
                && playerFactionIds.Contains(worldObject.Faction!));
    }

}

public sealed record SnapshotColonyAnchor(string? MapUniqueId, string? WorldObjectId, int Tile, int TileLayerId, string? Label);
