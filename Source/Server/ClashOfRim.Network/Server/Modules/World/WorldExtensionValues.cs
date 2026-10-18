using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class WorldExtensionValues
{
    internal static bool Equal(
        IReadOnlyList<WorldConfigurationExtensionDto>? left,
        IReadOnlyList<WorldConfigurationExtensionDto>? right)
    {
        WorldConfigurationExtensionDto[] orderedLeft = OrderWorldExtensions(left);
        WorldConfigurationExtensionDto[] orderedRight = OrderWorldExtensions(right);
        if (orderedLeft.Length != orderedRight.Length)
        {
            return false;
        }

        for (int index = 0; index < orderedLeft.Length; index++)
        {
            WorldConfigurationExtensionDto leftExtension = orderedLeft[index];
            WorldConfigurationExtensionDto rightExtension = orderedRight[index];
            if (!string.Equals(leftExtension.ProviderId, rightExtension.ProviderId, StringComparison.Ordinal)
                || !string.Equals(leftExtension.Kind, rightExtension.Kind, StringComparison.Ordinal)
                || !string.Equals(leftExtension.SchemaVersion, rightExtension.SchemaVersion, StringComparison.Ordinal)
                || !string.Equals(leftExtension.PayloadJson, rightExtension.PayloadJson, StringComparison.Ordinal)
                || !MetadataEqual(leftExtension.Metadata, rightExtension.Metadata))
            {
                return false;
            }
        }

        return true;
    }

    private static WorldConfigurationExtensionDto[] OrderWorldExtensions(
        IReadOnlyList<WorldConfigurationExtensionDto>? extensions)
    {
        return (extensions ?? Array.Empty<WorldConfigurationExtensionDto>())
            .OrderBy(extension => extension.ProviderId, StringComparer.Ordinal)
            .ThenBy(extension => extension.Kind, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MetadataEqual(
        IReadOnlyDictionary<string, string?>? left,
        IReadOnlyDictionary<string, string?>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if ((left?.Count ?? 0) != (right?.Count ?? 0))
        {
            return false;
        }

        foreach ((string key, string? value) in left ?? new Dictionary<string, string?>())
        {
            if (right is null
                || !right.TryGetValue(key, out string? rightValue)
                || !string.Equals(value, rightValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
