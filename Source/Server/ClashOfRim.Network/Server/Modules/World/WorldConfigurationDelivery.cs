using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldConfigurationDelivery(ServerPluginRegistry plugins, CompatibilityBaselineRegistry compatibility)
{
    internal WorldConfigurationDto? Build(
        WorldConfigurationDto? configuration,
        bool includeGenerationBaseline = true,
        bool includePlayerColonySites = true,
        bool includeWorldExtensions = true)
    {
        if (configuration is null)
        {
            return null;
        }

        IReadOnlyList<PlayerColonySiteDto> occupiedSites = includePlayerColonySites
            ? WorldColonySites.Current(configuration)
            : Array.Empty<PlayerColonySiteDto>();
        IReadOnlyList<WorldConfigurationExtensionDto> extensions =
            plugins.ActiveWorldConfigurationExtensions(compatibility.Current).BuildDeliveryExtensions(
                includeGenerationBaseline ? configuration.Extensions : Array.Empty<WorldConfigurationExtensionDto>(),
                includeWorldExtensions);
        return new WorldConfigurationDto(
            configuration.WorldConfigurationId,
            configuration.ConfiguredByUserId,
            configuration.ConfiguredByColonyId,
            configuration.ConfiguredAtUtc,
            includeGenerationBaseline ? configuration.SeedString : null,
            includeGenerationBaseline ? configuration.PlanetCoverage : null,
            includeGenerationBaseline ? configuration.OverallRainfall : null,
            includeGenerationBaseline ? configuration.OverallTemperature : null,
            includeGenerationBaseline ? configuration.OverallPopulation : null,
            includeGenerationBaseline ? configuration.LandmarkDensity : null,
            includeGenerationBaseline ? configuration.TileCount : null,
            includeGenerationBaseline ? configuration.FactionDefNames : Array.Empty<string>(),
            includeGenerationBaseline ? configuration.Features : Array.Empty<WorldFeatureDto>(),
            includeGenerationBaseline ? configuration.Factions : Array.Empty<WorldFactionDto>(),
            includeGenerationBaseline ? configuration.Roads : Array.Empty<WorldRoadDto>(),
            includeGenerationBaseline ? configuration.WorldObjects : Array.Empty<WorldObjectBaselineDto>(),
            occupiedSites,
            configuration.StorytellerDefName,
            configuration.DifficultyDefName,
            tileGeometry: null,
            difficultyValuesXml: configuration.DifficultyValuesXml,
            extensions: extensions,
            gameLanguage: configuration.GameLanguage,
            featureNameCatalogs: includeGenerationBaseline ? configuration.FeatureNameCatalogs : Array.Empty<WorldFeatureNameCatalogDto>());
    }
}
