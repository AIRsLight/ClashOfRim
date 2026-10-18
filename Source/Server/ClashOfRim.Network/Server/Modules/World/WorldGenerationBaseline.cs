using System.Globalization;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldGenerationBaseline
{
    // Shared by request validation and stored-world readiness; return a localization key.
    internal static string? ValidationFailure(WorldConfigurationDto configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.SeedString)) return "WorldConfiguration.MissingSeed";
        if (!float.TryParse(configuration.PlanetCoverage, NumberStyles.Float, CultureInfo.InvariantCulture, out float coverage)
            || !float.IsFinite(coverage) || coverage <= 0f) return "WorldConfiguration.MissingPlanetCoverage";
        return configuration.FactionDefNames.Count == 0 ? "WorldConfiguration.MissingFactions" : null;
    }
}
