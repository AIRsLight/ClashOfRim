using AIRsLight.ClashOfRim.Network.Modules.World;
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
            if (WorldExtensionValues.Equal(current.Extensions, merged))
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

}
