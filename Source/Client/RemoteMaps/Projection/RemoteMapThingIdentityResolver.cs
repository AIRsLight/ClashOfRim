using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class RemoteMapThingIdentityResolver
{
    public static bool TryResolveOriginalThingId(
        IEnumerable<RemoteMapThingIdentityRecord>? identities,
        string? mapUniqueId,
        string? projectedThingId,
        out string originalThingId)
    {
        originalThingId = string.Empty;
        if (identities is null)
        {
            return false;
        }

        string normalizedMapId = NormalizeMapId(mapUniqueId);
        string normalizedProjectedThingId = NormalizeThingId(projectedThingId);
        if (string.IsNullOrWhiteSpace(normalizedMapId)
            || string.IsNullOrWhiteSpace(normalizedProjectedThingId))
        {
            return false;
        }

        foreach (RemoteMapThingIdentityRecord identity in identities)
        {
            if (!string.Equals(NormalizeMapId(identity.MapUniqueId), normalizedMapId, StringComparison.Ordinal)
                || !string.Equals(
                    NormalizeThingId(identity.ProjectedThingId),
                    normalizedProjectedThingId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            originalThingId = NormalizeThingId(identity.OriginalThingId);
            return !string.IsNullOrWhiteSpace(originalThingId);
        }

        return false;
    }

    private static string NormalizeMapId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed.Substring("Map_".Length)
            : trimmed;
    }

    private static string NormalizeThingId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("Thing_", StringComparison.Ordinal)
            ? trimmed.Substring("Thing_".Length)
            : trimmed;
    }
}
