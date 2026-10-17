using System;

namespace AIRsLight.ClashOfRim.Modules.Maps;

internal static class ClientMapIdentity
{
    // Wire target IDs accept the historical numeric form; current map IDs are canonical.
    internal static bool MapIdsMatch(string currentMapId, string? targetMapId)
    {
        if (string.IsNullOrWhiteSpace(targetMapId)) return false;
        string normalizedTarget = targetMapId!.StartsWith("Map_", StringComparison.Ordinal) ? targetMapId : "Map_" + targetMapId;
        return string.Equals(currentMapId, normalizedTarget, StringComparison.Ordinal);
    }
}
