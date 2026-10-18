using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldBaselineRuntime : IClientWorldBaselineRuntime
{
    public IClientWorldBaselineSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientWorldBaselineSession
    {
        public bool IsCurrent => Game is not null && World is not null
            && ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public string? Language => ClientWorldNameRuntime.ReadCurrentGameLanguage();
        public ModWorldConfigurationDto ReadWorld(string userId, string colonyId, ModColonyAppearanceDto? appearance)
        {
            var world = World!;
            object? info = ReadMember(world, "info") ?? ReadMember(world, "Info");
            var player = world.factionManager?.OfPlayer;
            var configuration = new ModWorldConfigurationDto
            {
                SeedString = ReadFirstString(info, "seedString", "SeedString", "seed"),
                PlanetCoverage = ReadFirstString(info, "planetCoverage", "PlanetCoverage"),
                OverallRainfall = ReadFirstString(info, "overallRainfall", "OverallRainfall"),
                OverallTemperature = ReadFirstString(info, "overallTemperature", "OverallTemperature"),
                OverallPopulation = ReadFirstString(info, "overallPopulation", "OverallPopulation"),
                LandmarkDensity = ReadFirstString(info, "landmarkDensity", "LandmarkDensity"),
                TileCount = ReadFirstString(world.grid, "TilesCount", "tilesCount"),
                GameLanguage = ClientWorldNameRuntime.ReadCurrentGameLanguage()
            };
            configuration.FactionDefNames.AddRange(ReadWorldFactionDefNames(world, player));
            configuration.Features.AddRange(ClientWorldFeaturesRuntime.ReadWorldFeatures(world.features));
            configuration.Factions.AddRange(ReadWorldFactions(world, player));
            configuration.Roads.AddRange(ReadWorldRoads(world));
            configuration.WorldObjects.AddRange(ReadWorldObjects(world, player));
            configuration.PlayerColonySites.AddRange(ClientWorldColonySitesRuntime.Capture(Game, world, userId, colonyId, appearance));
            configuration.TileGeometry = ReadWorldTileGeometry(world.grid);
            return configuration;
        }
        public IReadOnlyList<ModWorldConfigurationExtensionDto> ReadExtensions(string userId, string colonyId, string worldId)
            => ClashOfRimCompatibilityApi.CollectCurrentWorldConfigurationExtensions(userId, colonyId, worldId).ToArray();
        public byte[]? CaptureSubstrate(ModWorldTileGeometryDto? geometry, out string? failure)
            => WorldSubstrateRuntime.TryCapture(World, geometry, () => IsCurrent, out var bytes, out failure) ? bytes : null;
    }
    private static List<string> ReadWorldFactionDefNames(World world, Faction? player)
    {
        object? worldInfo = ReadMember(world, "info") ?? ReadMember(world, "Info");
        if (ReadMember(worldInfo, "factions") is IEnumerable<FactionDef> worldInfoFactions)
        {
            List<string> configuredFactionDefs = worldInfoFactions
                .Where(def => def != null)
                .Select(def => def.defName)
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .ToList();
            if (configuredFactionDefs.Count > 0)
            {
                return configuredFactionDefs;
            }
        }

        return world?.factionManager?.AllFactions
            .Where(faction => faction != null && faction != player && faction.def != null)
            .Select(faction => faction.def.defName)
            .Where(defName => !string.IsNullOrWhiteSpace(defName))
            .ToList() ?? new List<string>();
    }

    private static List<ModWorldFactionDto> ReadWorldFactions(World world, Faction? player)
    {
        return world?.factionManager?.AllFactions
            .Where(faction => faction != null && faction != player && faction.def != null)
            .Select(faction => new ModWorldFactionDto
            {
                DefName = faction.def.defName,
                Name = faction.Name,
                ColorR = faction.Color.r,
                ColorG = faction.Color.g,
                ColorB = faction.Color.b,
                ColorA = faction.Color.a
            })
            .ToList() ?? new List<ModWorldFactionDto>();
    }

    private static List<ModWorldRoadDto> ReadWorldRoads(World world)
    {
        List<ModWorldRoadDto> roads = new();
        foreach (SurfaceTile tile in world.grid.Tiles)
        {
            if (tile.Roads is null)
            {
                continue;
            }

            foreach (SurfaceTile.RoadLink link in tile.Roads)
            {
                if (link.road is null || roads.Any(road =>
                    road.FromTile == tile.tile && road.ToTile == link.neighbor ||
                    road.FromTile == link.neighbor && road.ToTile == tile.tile))
                {
                    continue;
                }

                roads.Add(new ModWorldRoadDto
                {
                    FromTile = tile.tile,
                    ToTile = link.neighbor,
                    RoadDefName = link.road.defName
                });
            }
        }

        return roads;
    }

    private static List<ModWorldObjectBaselineDto> ReadWorldObjects(World world, Faction? player)
    {
        List<ModWorldObjectBaselineDto> worldObjects = new();
        foreach (WorldObject worldObject in world.worldObjects.AllWorldObjects)
        {
            if (worldObject.def is null || worldObject.Faction == player)
            {
                continue;
            }

            string? label = worldObject is Settlement settlement
                ? settlement.Name
                : Convert.ToString(ReadMember(worldObject, "Name") ?? ReadMember(worldObject, "Label"), CultureInfo.InvariantCulture);
            worldObjects.Add(new ModWorldObjectBaselineDto
            {
                DefName = worldObject.def.defName,
                Tile = worldObject.Tile.tileId,
                TileLayerId = ReadPlanetTileLayerId(worldObject.Tile),
                Label = label,
                FactionDefName = worldObject.Faction?.def?.defName
            });
        }

        return worldObjects;
    }

    internal static ModWorldTileGeometryDto? ReadWorldTileGeometry(WorldGrid? grid)
    {
        if (grid?.PlanetLayers is null)
        {
            return null;
        }

        var geometry = new ModWorldTileGeometryDto();
        foreach (KeyValuePair<int, PlanetLayer> entry in grid.PlanetLayers.OrderBy(entry => entry.Key))
        {
            PlanetLayer layer = entry.Value;
            if (layer is null || layer.TilesCount <= 0 || layer.AverageTileSize <= 0f)
            {
                continue;
            }

            var layerDto = new ModWorldTileLayerGeometryDto
            {
                LayerId = layer.LayerID,
                LayerDefName = layer.Def?.defName,
                AverageTileSize = layer.AverageTileSize
            };

            for (int tile = 0; tile < layer.TilesCount; tile++)
            {
                Vector3 center = layer.GetTileCenter(tile);
                layerDto.TileCenters.Add(new ModWorldTileCenterDto
                {
                    Tile = tile,
                    X = center.x,
                    Y = center.y,
                    Z = center.z
                });
            }

            geometry.Layers.Add(layerDto);
        }

        return geometry.Layers.Count == 0 ? null : geometry;
    }
}
