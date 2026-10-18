using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldConfigurationRuntime : IClientWorldConfigurationRuntime
{
    public IClientWorldConfigurationSession Bind() => new Session(Current.Game, Find.World);
    private sealed record Session(Game? Game, World? World) : IClientWorldConfigurationSession
    {
        public bool IsCurrent => ReferenceEquals(Game, Current.Game) && ReferenceEquals(World, Find.World);
        public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration)
            => ClientWorldFeaturesRuntime.Apply(World, configuration, () => IsCurrent);
        public void ApplyFactions(ModWorldConfigurationDto configuration) => ApplyWorldFactions(configuration.Factions);
        public void ApplyRoads(ModWorldConfigurationDto configuration) => ApplyWorldRoads(configuration.Roads);
        public void ApplyObjectLabels(ModWorldConfigurationDto configuration) => ApplyWorldObjectLabels(configuration.WorldObjects);
        public void ApplyExtensions(ModWorldConfigurationDto configuration, string userId, bool worldState)
        {
            ClashOfRimCompatibilityApi.ApplyWorldConfigurationExtensions(configuration, userId, worldState);
            if (!IsCurrent) return;
            var owners = new HashSet<string>(configuration.PlayerColonySites.Where(site => !string.IsNullOrWhiteSpace(site.UserId) && site.UserId != userId).Select(site => site.UserId!), StringComparer.Ordinal);
            foreach (string owner in owners) { if (!IsCurrent) return; PlayerFactionProxyUtility.EnsureProxyForUser(owner); }
            if (IsCurrent) PlayerFactionProxyUtility.NormalizeExistingProxies(owners);
        }
    }
    internal static void RefreshWorldFeatureTexts()
    {
        if (Find.WorldFeatures is null)
        {
            return;
        }

        Find.WorldFeatures.textsCreated = false;
        Find.WorldFeatures.UpdateFeatures();
    }

    internal static void ApplyWorldFactions(IReadOnlyList<ModWorldFactionDto> factions)
    {
        var manager = Find.World?.factionManager;
        if (manager is null) return;
        foreach (ModWorldFactionDto baseline in factions)
        {
            Faction? faction = manager.AllFactions
                .FirstOrDefault(item => item.def?.defName == baseline.DefName);
            if (faction is null)
            {
                faction = CreateWorldBaselineFaction(baseline, manager);
                if (faction is null)
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(baseline.Name))
            {
                faction.Name = baseline.Name;
            }

            faction.color = new Color(baseline.ColorR, baseline.ColorG, baseline.ColorB, baseline.ColorA);
        }
    }

    internal static Faction? CreateWorldBaselineFaction(ModWorldFactionDto baseline, FactionManager manager)
    {
        if (string.IsNullOrWhiteSpace(baseline.DefName))
        {
            return null;
        }

        FactionDef? factionDef = DefDatabase<FactionDef>.GetNamedSilentFail(baseline.DefName);
        if (factionDef is null || factionDef.isPlayer)
        {
            return null;
        }

        Faction faction = new()
        {
            def = factionDef,
            loadID = Find.UniqueIDsManager.GetNextFactionID(),
            hidden = false,
            color = new Color(baseline.ColorR, baseline.ColorG, baseline.ColorB, baseline.ColorA)
        };
        faction.Name = !string.IsNullOrWhiteSpace(baseline.Name)
            ? baseline.Name
            : factionDef.LabelCap;
        ClashOfRimCompatibilityApi.NotifyFactionPrepared(faction, "WorldBaseline");

        foreach (Faction other in manager.AllFactionsListForReading.ToList())
        {
            if (other is null || other == faction)
            {
                continue;
            }

            faction.SetRelation(new FactionRelation(other, FactionRelationKind.Neutral));
            other.SetRelation(new FactionRelation(faction, FactionRelationKind.Neutral));
        }

        manager.Add(faction);
        ClashLog.Message("[ClashOfRim] Created server baseline faction: " + baseline.DefName + ".");
        return faction;
    }

    internal static void ApplyWorldRoads(IReadOnlyList<ModWorldRoadDto> roads)
    {
        var grid = Find.WorldGrid;
        PlanetLayer layer = grid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
        foreach (SurfaceTile tile in layer.Tiles)
        {
            tile.Roads?.Clear();
            tile.potentialRoads = null;
        }

        foreach (ModWorldRoadDto road in roads)
        {
            if (road.FromTile < 0 || road.FromTile >= grid.TilesCount || road.ToTile < 0 || road.ToTile >= grid.TilesCount)
            {
                continue;
            }

            RoadDef? roadDef = DefDatabase<RoadDef>.GetNamedSilentFail(road.RoadDefName);
            if (roadDef is null)
            {
                continue;
            }

            AddRoadLink(grid[road.FromTile], road.ToTile, roadDef);
            AddRoadLink(grid[road.ToTile], road.FromTile, roadDef);
        }
    }

    internal static void AddRoadLink(SurfaceTile tile, int neighborTile, RoadDef roadDef)
    {
        SurfaceTile.RoadLink link = new()
        {
            neighbor = neighborTile,
            road = roadDef
        };
        tile.potentialRoads ??= new List<SurfaceTile.RoadLink>();
        tile.potentialRoads.Add(link);
    }

    internal static void ApplyWorldObjectLabels(IReadOnlyList<ModWorldObjectBaselineDto> worldObjects)
    {
        var objects = Find.WorldObjects.AllWorldObjects;
        var factions = Find.World.factionManager.AllFactions.ToList();
        foreach (ModWorldObjectBaselineDto baseline in worldObjects)
        {
            WorldObject? worldObject = objects
                .FirstOrDefault(item => item.Tile == baseline.Tile && item.def?.defName == baseline.DefName);
            if (worldObject is null)
            {
                continue;
            }

            Faction? faction = string.IsNullOrWhiteSpace(baseline.FactionDefName)
                ? null
                : factions.FirstOrDefault(item => item.def?.defName == baseline.FactionDefName);
            if (faction is not null)
            {
                worldObject.SetFaction(faction);
            }

            if (!string.IsNullOrWhiteSpace(baseline.Label) && worldObject is Settlement settlement)
            {
                settlement.Name = baseline.Label;
            }
        }
    }

    internal static bool IsValidTile(int tile)
    {
        return tile >= 0 && tile < Find.WorldGrid.TilesCount;
    }

}
