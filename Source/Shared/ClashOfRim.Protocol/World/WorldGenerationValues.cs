using System.Globalization;

namespace AIRsLight.ClashOfRim.Protocol;

// Preserve the established wire value/unit. Engine-specific ranges must not be
// invented independently by a client after the server has accepted a baseline.
public static class WorldGenerationValues
{
    public static bool TryCoverage(string? text, out float coverage)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out coverage)
            && !float.IsNaN(coverage) && !float.IsInfinity(coverage) && coverage > 0f;
}
