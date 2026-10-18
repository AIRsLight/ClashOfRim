using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldGenerationBaseline
{
    // Shared by request validation and stored-world readiness; return a localization key.
    internal static string? ValidationFailure(WorldConfigurationDto configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.SeedString)) return "WorldConfiguration.MissingSeed";
        if (!WorldGenerationValues.TryCoverage(configuration.PlanetCoverage, out _)) return "WorldConfiguration.MissingPlanetCoverage";
        return configuration.FactionDefNames.Count == 0 ? "WorldConfiguration.MissingFactions" : null;
    }
}
