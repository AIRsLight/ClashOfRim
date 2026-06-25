using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class BiotechWorldConfigurationCompatibility
{
    internal static IReadOnlyList<ModWorldPollutedTileDto> ReadWorldPollution(ModWorldConfigurationDto configuration)
    {
        return WorldConfigurationExtensionPayloadJson.Read<ModWorldPollutedTileDto>(
            configuration.Extensions,
            BiotechCompatibilityKeys.PackageId,
            BiotechCompatibilityKeys.WorldPollution);
    }

    internal static float ReadWorldGenerationPollution(ModWorldConfigurationDto configuration)
    {
        ModBiotechWorldGenerationDto? settings =
            WorldConfigurationExtensionPayloadJson.Read<ModBiotechWorldGenerationDto>(
                    configuration.Extensions,
                    BiotechCompatibilityKeys.PackageId,
                    BiotechCompatibilityKeys.WorldGeneration)
                .FirstOrDefault();
        return float.TryParse(
            settings?.Pollution,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float pollution)
            ? Math.Max(0f, pollution)
            : 0f;
    }

    internal static bool TryResolveWorldGenerationPollution(
        ModWorldConfigurationDto configuration,
        out float value)
    {
        value = ReadWorldGenerationPollution(configuration);
        return true;
    }

    internal static IReadOnlyList<ModWorldConfigurationExtensionDto> CollectWorldGenerationExtension(
        string? userId,
        string? colonyId,
        string worldConfigurationId)
    {
        ModWorldConfigurationExtensionDto? extension = BuildWorldGenerationExtension(ReadCurrentWorldGenerationSettings());
        return extension is null
            ? Array.Empty<ModWorldConfigurationExtensionDto>()
            : new[] { extension };
    }

    internal static IReadOnlyList<ModWorldConfigurationExtensionDto> CollectWorldPollutionExtension(
        string? userId,
        string? colonyId,
        string worldConfigurationId)
    {
        ModWorldConfigurationExtensionDto? extension = BuildWorldPollutionExtension(ReadCurrentWorldPollutedTiles());
        return extension is null
            ? Array.Empty<ModWorldConfigurationExtensionDto>()
            : new[] { extension };
    }

    internal static int ApplyWorldPollutionExtension(
        ModWorldConfigurationDto configuration,
        string? localUserId,
        bool applyWorldState)
    {
        if (!applyWorldState)
        {
            return 0;
        }

        IReadOnlyList<ModWorldPollutedTileDto> pollutedTiles = ReadWorldPollution(configuration);
        PlanetLayer layer = Find.World.grid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
        foreach (SurfaceTile tile in layer.Tiles)
        {
            tile.pollution = 0f;
        }

        foreach (ModWorldPollutedTileDto pollutedTile in pollutedTiles)
        {
            if (pollutedTile.Tile >= 0 && pollutedTile.Tile < Find.WorldGrid.TilesCount)
            {
                Find.WorldGrid[pollutedTile.Tile].pollution = pollutedTile.Pollution;
            }
        }

        return pollutedTiles.Count;
    }

    internal static IReadOnlyList<WorldConfigurationExtensionSummaryItem> WorldPollutionSummary(
        ModWorldConfigurationDto configuration)
    {
        List<WorldConfigurationExtensionSummaryItem> items = new()
        {
            new WorldConfigurationExtensionSummaryItem(
                "rimworld.biotech.world-pollution.tile-count",
                ClashOfRimText.Key("ClashOfRim.WorldCatalog.SummaryPollutedTiles"),
                ReadWorldPollution(configuration).Count.ToString(CultureInfo.InvariantCulture))
        };
        float generationPollution = ReadWorldGenerationPollution(configuration);
        if (generationPollution > 0f)
        {
            items.Add(new WorldConfigurationExtensionSummaryItem(
                "rimworld.biotech.world-generation.pollution",
                ClashOfRimText.Key("ClashOfRim.WorldCatalog.SummaryPollution"),
                generationPollution.ToString(CultureInfo.InvariantCulture)));
        }

        return items;
    }

    private static ModWorldConfigurationExtensionDto? BuildWorldGenerationExtension(
        ModBiotechWorldGenerationDto? settings)
    {
        string? pollution = settings?.Pollution;
        if (string.IsNullOrWhiteSpace(pollution))
        {
            return null;
        }

        return new ModWorldConfigurationExtensionDto
        {
            ProviderId = BiotechCompatibilityKeys.PackageId,
            Kind = BiotechCompatibilityKeys.WorldGeneration,
            SchemaVersion = "1",
            PayloadJson = WorldConfigurationExtensionPayloadJson.Serialize(new[] { settings }),
            Metadata = new Dictionary<string, string?>
            {
                ["pollution"] = pollution
            }
        };
    }

    private static ModWorldConfigurationExtensionDto? BuildWorldPollutionExtension(
        IReadOnlyList<ModWorldPollutedTileDto> pollutedTiles)
    {
        if (pollutedTiles.Count == 0)
        {
            return null;
        }

        return new ModWorldConfigurationExtensionDto
        {
            ProviderId = BiotechCompatibilityKeys.PackageId,
            Kind = BiotechCompatibilityKeys.WorldPollution,
            SchemaVersion = "1",
            PayloadJson = WorldConfigurationExtensionPayloadJson.Serialize(pollutedTiles),
            Metadata = new Dictionary<string, string?>
            {
                ["count"] = pollutedTiles.Count.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    private static List<ModWorldPollutedTileDto> ReadCurrentWorldPollutedTiles()
    {
        var pollutedTiles = new List<ModWorldPollutedTileDto>();
        if (Find.WorldGrid?.Tiles is null)
        {
            return pollutedTiles;
        }

        foreach (SurfaceTile tile in Find.WorldGrid.Tiles)
        {
            if (tile.pollution == 0f)
            {
                continue;
            }

            pollutedTiles.Add(new ModWorldPollutedTileDto
            {
                Tile = tile.tile,
                Pollution = tile.pollution
            });
        }

        return pollutedTiles;
    }

    private static ModBiotechWorldGenerationDto? ReadCurrentWorldGenerationSettings()
    {
        object? world = Find.World;
        object? worldInfo = ReadMember(world, "info") ?? ReadMember(world, "Info");
        string? pollution = ReadFirstString(worldInfo, "pollution", "Pollution");
        return string.IsNullOrWhiteSpace(pollution)
            ? null
            : new ModBiotechWorldGenerationDto { Pollution = pollution };
    }

    private static object? ReadMember(object? instance, string memberName)
    {
        if (instance is null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        Type type = instance.GetType();
        return type.GetProperty(memberName)?.GetValue(instance)
            ?? type.GetField(memberName)?.GetValue(instance);
    }

    private static string? ReadFirstString(object? instance, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            object? value = ReadMember(instance, memberName);
            if (value is null)
            {
                continue;
            }

            return value switch
            {
                string text => text,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }

        return null;
    }
}
