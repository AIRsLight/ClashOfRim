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
        var (extensions, extensionsChanged) = PrepareExtensions(upload.AcceptedSnapshot, userId, colonyId, currentConfiguration, activeWorldExtensions);

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

    // Startup rebuilds extensions in a deterministic order without registering locations or sending live notifications.
    internal int RecoverExtensions(IEnumerable<LatestSnapshotRecord> snapshots)
    {
        var extensions = plugins.ActiveWorldConfigurationExtensions(baseline.Current);
        int recovered = 0;
        foreach (var snapshot in snapshots.OrderBy(item => item.AcceptedAtUtc)
                     .ThenBy(item => item.Identity.OwnerId, StringComparer.Ordinal).ThenBy(item => item.Identity.ColonyId, StringComparer.Ordinal))
        {
            var current = world.Current;
            string? userId = snapshot.Identity.OwnerId, colonyId = snapshot.Identity.ColonyId;
            if (current is null || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId)) continue;
            var (incoming, changed) = PrepareExtensions(snapshot, userId, colonyId, current, extensions);
            if (!changed) continue;
            world.RegisterPlayerColonySites(userId, colonyId, [], incoming, extensions);
            recovered++;
        }
        return recovered;
    }

    private static (IReadOnlyList<WorldConfigurationExtensionDto> Incoming, bool Changed) PrepareExtensions(LatestSnapshotRecord snapshot,
        string userId, string colonyId, WorldConfigurationDto configuration, WorldConfigurationExtensionService extensions)
    {
        var incoming = extensions.BuildExtensionsFromAcceptedSnapshot(new(snapshot, userId, colonyId, configuration.Extensions));
        var merged = extensions.MergeExtensions(new(userId, colonyId, configuration.WorldConfigurationId), configuration.Extensions, incoming);
        return (incoming, !WorldExtensionValues.Equal(configuration.Extensions, merged));
    }
}
