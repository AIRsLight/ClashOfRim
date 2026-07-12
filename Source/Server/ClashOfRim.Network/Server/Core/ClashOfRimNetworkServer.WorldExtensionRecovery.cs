using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static int RecoverWorldConfigurationExtensionsFromLatestSnapshots(ClashOfRimNetworkState state)
    {
        WorldConfigurationExtensionService activeWorldExtensions = ActiveWorldConfigurationExtensions(state);
        int recoveredSnapshots = 0;
        foreach (LatestSnapshotRecord snapshot in state.SnapshotStore.ListLatest()
                     .OrderBy(snapshot => snapshot.AcceptedAtUtc)
                     .ThenBy(snapshot => snapshot.Identity.OwnerId, StringComparer.Ordinal)
                     .ThenBy(snapshot => snapshot.Identity.ColonyId, StringComparer.Ordinal))
        {
            string? userId = snapshot.Identity.OwnerId;
            string? colonyId = snapshot.Identity.ColonyId;
            WorldConfigurationDto? current = state.WorldConfiguration.Current;
            if (current is null
                || string.IsNullOrWhiteSpace(userId)
                || string.IsNullOrWhiteSpace(colonyId))
            {
                continue;
            }

            IReadOnlyList<WorldConfigurationExtensionDto> incoming =
                activeWorldExtensions.BuildExtensionsFromAcceptedSnapshot(
                    new WorldConfigurationExtensionSnapshotContext(
                        snapshot,
                        userId!,
                        colonyId!,
                        current.Extensions));
            IReadOnlyList<WorldConfigurationExtensionDto> merged = activeWorldExtensions.MergeExtensions(
                new WorldConfigurationExtensionContext(
                    userId!,
                    colonyId!,
                    current.WorldConfigurationId),
                current.Extensions,
                incoming);
            if (WorldConfigurationExtensionsEqual(current.Extensions, merged))
            {
                continue;
            }

            state.WorldConfiguration.RegisterPlayerColonySites(
                userId!,
                colonyId!,
                Array.Empty<PlayerColonySiteDto>(),
                incoming,
                activeWorldExtensions);
            recoveredSnapshots++;
        }

        return recoveredSnapshots;
    }

    private static bool WorldConfigurationExtensionsEqual(
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
