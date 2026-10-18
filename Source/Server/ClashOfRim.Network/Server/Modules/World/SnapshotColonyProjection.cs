using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static AIRsLight.ClashOfRim.Network.Modules.World.ColonySiteRules;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class SnapshotColonyProjection(WorldConfigurationRegistry world, ServerPluginRegistry plugins,
    CompatibilityBaselineRegistry baseline, SnapshotColonyLocations anchors, WorldConfigurationChanges changes)
{
    internal void Apply(
        string userId,
        string colonyId,
        SnapshotUploadResult upload)
    {
        WorldConfigurationDto? currentConfiguration = world.Current;
        if (!upload.Accepted || upload.AcceptedSnapshot is null || currentConfiguration is null)
        {
            return;
        }

        WorldConfigurationExtensionService activeWorldExtensions = plugins.ActiveWorldConfigurationExtensions(baseline.Current);
        IReadOnlyList<WorldConfigurationExtensionDto> extensions =
            activeWorldExtensions.BuildExtensionsFromAcceptedSnapshot(
                new WorldConfigurationExtensionSnapshotContext(
                    upload.AcceptedSnapshot,
                    userId,
                    colonyId,
                    currentConfiguration.Extensions));
        IReadOnlyList<WorldConfigurationExtensionDto> mergedExtensions = activeWorldExtensions.MergeExtensions(
            new WorldConfigurationExtensionContext(
                userId,
                colonyId,
                currentConfiguration.WorldConfigurationId),
            currentConfiguration.Extensions,
            extensions);
        bool extensionsChanged = !WorldExtensionValues.Equal(
            currentConfiguration.Extensions,
            mergedExtensions);

        IReadOnlyList<SnapshotColonyAnchor> colonyAnchors = anchors.Extract(upload.AcceptedSnapshot.Index);
        IReadOnlyList<PlayerColonySiteDto> normalizedSites = Array.Empty<PlayerColonySiteDto>();
        if (colonyAnchors.Count == 1)
        {
            SnapshotColonyAnchor anchor = colonyAnchors[0];
            PlayerColonySiteDto? existingSite = currentConfiguration.PlayerColonySites.FirstOrDefault(site =>
                string.Equals(site.UserId, userId, StringComparison.Ordinal)
                && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal));
            PlayerColonySiteDto site = new(
                userId,
                colonyId,
                anchor.WorldObjectId,
                anchor.MapUniqueId,
                anchor.Tile,
                string.IsNullOrWhiteSpace(anchor.Label) ? colonyId : anchor.Label,
                existingSite?.FactionName,
                existingSite?.Appearance,
                anchor.TileLayerId);
            IReadOnlyList<PlayerColonySiteDto> candidateSites = NormalizePlayerColonySites(
                userId,
                colonyId,
                new[] { site });
            if (candidateSites.Count == 1
                && FindExistingUserColonyConflict(currentConfiguration.PlayerColonySites, candidateSites, userId, colonyId) is null
                && FindExistingSameColonySiteOnDifferentTile(currentConfiguration.PlayerColonySites, candidateSites, userId, colonyId) is null
                && FindSameTilePlayerColonyConflict(currentConfiguration.PlayerColonySites, candidateSites, userId, colonyId) is null)
            {
                normalizedSites = candidateSites;
            }
        }

        if (normalizedSites.Count == 0 && !extensionsChanged)
        {
            return;
        }

        world.RegisterPlayerColonySites(
            userId,
            colonyId,
            normalizedSites,
            extensions,
            activeWorldExtensions);
        changes.Signal(userId);
    }

}
