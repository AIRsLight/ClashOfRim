using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;

namespace AIRsLight.ClashOfRim;

internal static class ClientWorldColonySitesRuntime
{
    internal static List<ModPlayerColonySiteDto> Capture(Game? game, World? world, string? userId, string? colonyId, ModColonyAppearanceDto? appearance)
    {
        var maps = game?.Maps?.ToArray() ?? Array.Empty<Map>();
        var objects = world?.worldObjects?.AllWorldObjects?.ToArray() ?? Array.Empty<WorldObject>();
        var currentMap = game?.CurrentMap;
        var player = world?.factionManager?.OfPlayer;
        var layerSizes = world?.grid?.PlanetLayers?.ToDictionary(entry => entry.Key, entry => entry.Value.TilesCount) ?? new Dictionary<int, int>();
        var mapSites = new List<ModPlayerColonySiteDto>();
        var objectSites = new List<ModPlayerColonySiteDto>();
        ModPlayerColonySiteDto Site(object? parent, int tile, int layer, string? map) => new()
        {
            UserId = userId ?? "", ColonyId = colonyId ?? "", Tile = tile, TileLayerId = layer,
            WorldObjectId = ReadWorldObjectId(parent, tile, layer), MapUniqueId = map,
            Label = ReadPlayerColonyLabel(parent, colonyId),
            FactionName = string.IsNullOrWhiteSpace(player?.Name) ? null : player.Name, Appearance = appearance
        };
        foreach (var map in maps)
        {
            if (map is null || !IsPlayerColonyMap(map, player, currentMap)) continue;
            int? tile = ReadFirstInt(map, "Tile", "tile");
            if (!tile.HasValue) continue;
            var parent = ReadMember(map, "Parent") ?? ReadMember(map, "parent");
            mapSites.Add(Site(parent, tile.Value, ReadFirstTileLayerId(map, "Tile", "tile"), "Map_" + map.uniqueID.ToString(CultureInfo.InvariantCulture)));
        }
        foreach (var item in objects)
        {
            if (!IsPlayerColonyWorldObject(item, player)) continue;
            int? tile = ReadFirstInt(item, "Tile", "tile");
            if (tile.HasValue) objectSites.Add(Site(item, tile.Value, ReadFirstTileLayerId(item, "Tile", "tile"), ReadMapUniqueIdFromWorldObject(item)));
        }
        return ClientWorldColonySites.Merge(mapSites, objectSites, layerSizes);
    }
    internal static bool IsPlayerColonyMap(Map map) => IsPlayerColonyMap(map, Faction.OfPlayer, Find.CurrentMap);
    internal static bool IsPlayerColonyWorldObject(WorldObject? item) => IsPlayerColonyWorldObject(item, Faction.OfPlayer);
    private static bool IsPlayerColonyMap(Map map, Faction? player, Map? currentMap)
    {
        bool? isPlayerHome = ReadFirstBool(map, "IsPlayerHome", "isPlayerHome");
        if (isPlayerHome.HasValue)
        {
            return isPlayerHome.Value;
        }

        object? parent = ReadMember(map, "Parent") ?? ReadMember(map, "parent");
        return IsPlayerColonyWorldObject(parent as WorldObject, player) || ReferenceEquals(map, currentMap);
    }

    private static bool IsPlayerColonyWorldObject(WorldObject? worldObject, Faction? player)
    {
        if (worldObject is null || worldObject.Destroyed)
        {
            return false;
        }

        string? defName = ReadFirstString(ReadMember(worldObject, "def"), "defName", "Name");
        if (string.Equals(defName, "PlayerColony", StringComparison.Ordinal))
        {
            return true;
        }

        if (worldObject is not Settlement)
        {
            return false;
        }

        object? faction = ReadMember(worldObject, "Faction") ?? ReadMember(worldObject, "factionInt");
        return ReferenceEquals(faction, player);
    }

    private static string? ReadMapUniqueIdFromWorldObject(WorldObject worldObject)
    {
        object? map = ReadMember(worldObject, "Map") ?? ReadMember(worldObject, "map");
        int? uniqueId = ReadFirstInt(map, "uniqueID", "UniqueID", "uniqueId");
        return uniqueId.HasValue ? "Map_" + uniqueId.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    internal static string? ReadWorldObjectId(object? worldObject, int tile, int tileLayerId = 0)
    {
        string? id = ReadFirstString(worldObject, "UniqueLoadID", "uniqueLoadID", "ID", "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id!.StartsWith("WorldObject_", StringComparison.Ordinal)
                ? id
                : "WorldObject_" + id;
        }

        return "tile:" + tile.ToString(CultureInfo.InvariantCulture)
            + ","
            + Math.Max(0, tileLayerId).ToString(CultureInfo.InvariantCulture);
    }

    private static string? ReadPlayerColonyLabel(object? parent, string? colonyId)
    {
        string? label = ReadFirstString(parent, "Name", "Label", "label");
        return string.IsNullOrWhiteSpace(label) ? colonyId : label;
    }
}
