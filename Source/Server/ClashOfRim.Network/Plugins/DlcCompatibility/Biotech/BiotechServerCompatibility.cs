using AIRsLight.ClashOfRim.Protocol;
using System.Globalization;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal static class BiotechServerCompatibility
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public static ClashOfRimServerPluginDescriptor Descriptor { get; } =
        new(
            Id: "builtin.dlc." + BiotechCompatibilityKeys.PackageId,
            Name: "Built-in DLC compatibility: Biotech",
            Version: "1.0.0",
            AssemblyName: "AIRsLight.ClashOfRim.Network",
            FileName: string.Empty,
            Capabilities: new[]
            {
                "gene-and-xenotype-sync",
                BiotechCompatibilityKeys.PawnExchange,
                BiotechCompatibilityKeys.TradeMetadata,
                BiotechCompatibilityKeys.WorldGeneration,
                BiotechCompatibilityKeys.WorldPollution
            },
            TradeThingMetadataMatchers: new ITradeThingMetadataMatcher[] { new BiotechTradeThingMetadataMatcher() },
            WorldConfigurationExtensionProviders: new IWorldConfigurationExtensionProvider[]
            {
                new BiotechWorldPollutionExtensionProvider()
            },
            IgnoredRaidSettlementThingDefNames: new[] { "ClashOfRim_MechDefensePoint" },
            RequiredPackageIds: new[] { BiotechCompatibilityKeys.PackageId },
            Order: 300);

    public static IReadOnlyList<WorldPollutedTileDto> ReadWorldPollution(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        return ReadWorldPollutionExtension(extensions);
    }

    public static BiotechWorldGenerationSettingsDto? ReadWorldGenerationSettings(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        WorldConfigurationExtensionDto? extension = extensions?.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, BiotechCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, BiotechCompatibilityKeys.WorldGeneration, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(extension?.PayloadJson))
        {
            return null;
        }

        try
        {
            List<BiotechWorldGenerationSettingsDto>? parsed = JsonSerializer.Deserialize<List<BiotechWorldGenerationSettingsDto>>(
                extension.PayloadJson!,
                PayloadJsonOptions);
            return parsed?.FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static WorldConfigurationExtensionDto? BuildWorldGenerationExtension(
        BiotechWorldGenerationSettingsDto? settings)
    {
        if (string.IsNullOrWhiteSpace(settings?.Pollution))
        {
            return null;
        }

        return new WorldConfigurationExtensionDto(
            BiotechCompatibilityKeys.PackageId,
            BiotechCompatibilityKeys.WorldGeneration,
            "1",
            JsonSerializer.Serialize(new[] { settings }, PayloadJsonOptions),
            new Dictionary<string, string?>
            {
                ["pollution"] = settings.Pollution
            });
    }

    public static WorldConfigurationExtensionDto? BuildWorldPollutionExtension(
        IReadOnlyList<WorldPollutedTileDto> pollutedTiles)
    {
        if (pollutedTiles.Count == 0)
        {
            return null;
        }

        return new WorldConfigurationExtensionDto(
            BiotechCompatibilityKeys.PackageId,
            BiotechCompatibilityKeys.WorldPollution,
            "1",
            JsonSerializer.Serialize(pollutedTiles, PayloadJsonOptions),
            new Dictionary<string, string?>
            {
                ["count"] = pollutedTiles.Count.ToString(CultureInfo.InvariantCulture)
            });
    }

    internal static IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensions(
        BiotechWorldGenerationSettingsDto? settings,
        IReadOnlyList<WorldPollutedTileDto> pollutedTiles)
    {
        var extensions = new List<WorldConfigurationExtensionDto>();
        WorldConfigurationExtensionDto? generation = BuildWorldGenerationExtension(settings);
        if (generation is not null)
        {
            extensions.Add(generation);
        }

        WorldConfigurationExtensionDto? pollution = BuildWorldPollutionExtension(pollutedTiles);
        if (pollution is not null)
        {
            extensions.Add(pollution);
        }

        return extensions;
    }

    private static IReadOnlyList<WorldPollutedTileDto> ReadWorldPollutionExtension(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        WorldConfigurationExtensionDto? extension = extensions?.FirstOrDefault(extension =>
            string.Equals(extension.ProviderId, BiotechCompatibilityKeys.PackageId, StringComparison.Ordinal)
            && string.Equals(extension.Kind, BiotechCompatibilityKeys.WorldPollution, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(extension?.PayloadJson))
        {
            return Array.Empty<WorldPollutedTileDto>();
        }

        try
        {
            List<WorldPollutedTileDto>? parsed = JsonSerializer.Deserialize<List<WorldPollutedTileDto>>(
                extension.PayloadJson!,
                PayloadJsonOptions);
            return parsed is null ? Array.Empty<WorldPollutedTileDto>() : parsed;
        }
        catch (JsonException)
        {
            return Array.Empty<WorldPollutedTileDto>();
        }
    }

}
