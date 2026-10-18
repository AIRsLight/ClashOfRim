using System;
using System.Collections.Generic;
using System.Globalization;
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
        {
            var features = ApplyWorldFeatures(configuration, out var language);
            return features is { Count: > 0 } && !string.IsNullOrWhiteSpace(language) ? new(language!, features) : null;
        }
        public ClientWorldFeatureNames? CaptureLocalizedFeatures(ModWorldConfigurationDto configuration)
            => ShouldUseLocalWorldFeatureLabels(configuration) ? new(ReadCurrentGameLanguage()!, ReadCurrentWorldFeatures()) : null;
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
    internal static bool ShouldUseLocalWorldFeatureLabels(ModWorldConfigurationDto configuration)
    {
        string? baselineLanguage = configuration.GameLanguage;
        string? currentLanguage = ReadCurrentGameLanguage();
        return !string.IsNullOrWhiteSpace(baselineLanguage)
            && !string.IsNullOrWhiteSpace(currentLanguage)
            && !string.Equals(baselineLanguage, currentLanguage, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<ModWorldFeatureDto>? ApplyWorldFeatures(
        ModWorldConfigurationDto configuration,
        out string? generatedFeatureNameCatalogLanguage)
    {
        generatedFeatureNameCatalogLanguage = null;
        IReadOnlyList<ModWorldFeatureDto> features = configuration.Features;
        var worldFeatures = Find.WorldFeatures;
        if (features.Count == 0 || worldFeatures?.features is null)
        {
            return null;
        }

        string? currentLanguage = ReadCurrentGameLanguage();
        ModWorldFeatureNameCatalogDto? featureNameCatalog = FindWorldFeatureNameCatalog(configuration, currentLanguage);
        bool useFeatureNameCatalog = IsWorldFeatureNameCatalogUsable(featureNameCatalog, features);
        bool generateLocalLanguageCatalog = !useFeatureNameCatalog && ShouldUseLocalWorldFeatureLabels(configuration);
        List<ModWorldFeatureDto>? generatedFeatureNameCatalog = generateLocalLanguageCatalog
            ? new List<ModWorldFeatureDto>(features.Count)
            : null;
        if (generateLocalLanguageCatalog)
        {
            generatedFeatureNameCatalogLanguage = currentLanguage;
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        worldFeatures.features.Clear();
        for (int index = 0; index < features.Count; index++)
        {
            ModWorldFeatureDto feature = features[index];
            FeatureDef? def = DefDatabase<FeatureDef>.GetNamedSilentFail(feature.DefName);
            if (def is null)
            {
                continue;
            }

            ModWorldFeatureDto labelFeature = useFeatureNameCatalog
                ? featureNameCatalog!.Features[index]
                : feature;
            string label = ResolveWorldFeatureLabel(
                def,
                labelFeature,
                index,
                generateLocalLanguageCatalog,
                usedNames);
            worldFeatures.features.Add(new WorldFeature
            {
                def = def,
                uniqueID = index,
                name = label,
                maxDrawSizeInTiles = feature.MaxDrawSizeInTiles,
                drawCenter = new Vector3(feature.DrawCenterX, feature.DrawCenterY, feature.DrawCenterZ),
                layer = PlanetLayer.Selected
            });
            generatedFeatureNameCatalog?.Add(CopyWorldFeatureLabel(feature, label));
        }

        worldFeatures.textsCreated = false;
        return generatedFeatureNameCatalog is { Count: > 0 }
            ? generatedFeatureNameCatalog
            : null;
    }

    internal static ModWorldFeatureNameCatalogDto? FindWorldFeatureNameCatalog(
        ModWorldConfigurationDto configuration,
        string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        string normalizedLanguage = language!.Trim();
        return configuration.FeatureNameCatalogs.FirstOrDefault(catalog =>
            string.Equals(catalog.Language?.Trim(), normalizedLanguage, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsWorldFeatureNameCatalogUsable(
        ModWorldFeatureNameCatalogDto? catalog,
        IReadOnlyList<ModWorldFeatureDto> features)
    {
        if (catalog is null || catalog.Features.Count != features.Count)
        {
            return false;
        }

        for (int index = 0; index < features.Count; index++)
        {
            ModWorldFeatureDto expected = features[index];
            ModWorldFeatureDto actual = catalog.Features[index];
            if (!string.Equals(expected.DefName, actual.DefName, StringComparison.Ordinal)
                || !NearlyEqual(expected.MaxDrawSizeInTiles, actual.MaxDrawSizeInTiles)
                || !NearlyEqual(expected.DrawCenterX, actual.DrawCenterX)
                || !NearlyEqual(expected.DrawCenterY, actual.DrawCenterY)
                || !NearlyEqual(expected.DrawCenterZ, actual.DrawCenterZ))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool NearlyEqual(float left, float right)
    {
        return Math.Abs(left - right) <= 0.001f;
    }

    internal static ModWorldFeatureDto CopyWorldFeatureLabel(ModWorldFeatureDto source, string label)
    {
        return new ModWorldFeatureDto
        {
            DefName = source.DefName,
            Label = label,
            MaxDrawSizeInTiles = source.MaxDrawSizeInTiles,
            DrawCenterX = source.DrawCenterX,
            DrawCenterY = source.DrawCenterY,
            DrawCenterZ = source.DrawCenterZ
        };
    }

    internal static string ResolveWorldFeatureLabel(
        FeatureDef def,
        ModWorldFeatureDto feature,
        int index,
        bool useLocalLanguageLabel,
        HashSet<string> usedNames)
    {
        if (useLocalLanguageLabel && TryGenerateLocalWorldFeatureName(def, feature, index, usedNames, out string generatedName))
        {
            return generatedName;
        }

        string label = string.IsNullOrWhiteSpace(feature.Label)
            ? def.label ?? def.defName
            : feature.Label!;
        usedNames.Add(label);
        return label;
    }

    internal static bool TryGenerateLocalWorldFeatureName(
        FeatureDef def,
        ModWorldFeatureDto feature,
        int index,
        HashSet<string> usedNames,
        out string name)
    {
        name = string.Empty;
        if (def.nameMaker is null)
        {
            return false;
        }

        int seed = GenText.StableStringHash(
            string.Join(
                "\u001f",
                def.defName,
                index.ToString(CultureInfo.InvariantCulture),
                feature.MaxDrawSizeInTiles.ToString(CultureInfo.InvariantCulture),
                feature.DrawCenterX.ToString(CultureInfo.InvariantCulture),
                feature.DrawCenterY.ToString(CultureInfo.InvariantCulture),
                feature.DrawCenterZ.ToString(CultureInfo.InvariantCulture),
                ReadCurrentGameLanguage() ?? string.Empty));
        Rand.PushState(seed);
        try
        {
            name = NameGenerator.GenerateName(
                def.nameMaker,
                candidate => !string.IsNullOrWhiteSpace(candidate) && !usedNames.Contains(candidate),
                appendNumberIfNameUsed: false,
                rootKeyword: "r_name");
        }
        catch (Exception ex)
        {
            Log.Warning($"[ClashOfRim] Failed to localize world feature name for {def.defName}: {ex.Message}");
            return false;
        }
        finally
        {
            Rand.PopState();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        usedNames.Add(name);
        return true;
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

    internal static string? ReadCurrentGameLanguage()
    {
        try
        {
            return LanguageDatabase.activeLanguage?.folderName
                ?? Prefs.LangFolderName;
        }
        catch
        {
            return null;
        }
    }

    internal static List<ModWorldFeatureDto> ReadCurrentWorldFeatures()
    {
        List<ModWorldFeatureDto> features = new();
        if (Find.WorldFeatures?.features is null)
        {
            return features;
        }

        foreach (WorldFeature feature in Find.WorldFeatures.features)
        {
            if (feature.def is null)
            {
                continue;
            }

            features.Add(new ModWorldFeatureDto
            {
                DefName = feature.def.defName,
                Label = feature.name,
                MaxDrawSizeInTiles = feature.maxDrawSizeInTiles,
                DrawCenterX = feature.drawCenter.x,
                DrawCenterY = feature.drawCenter.y,
                DrawCenterZ = feature.drawCenter.z
            });
        }

        return features;
    }

}
