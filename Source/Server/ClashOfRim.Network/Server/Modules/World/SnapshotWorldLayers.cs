using System.Xml;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class SnapshotWorldLayers(IColonySnapshotIndexStore snapshots, WorldConfigurationRegistry world,
    ServerPluginRegistry plugins, CompatibilityBaselineRegistry baseline, PlayerRegistry players,
    SnapshotMutationCoordinator mutations, WorldLayerNotifications notifications, Func<ILogger> logger)
{
    internal void Project(string userId, string colonyId, LatestSnapshotRecord? accepted, DateTimeOffset acceptedAtUtc)
    {
        using var mutation = mutations.EnterUnrecovered();
        notifications.Recover();
        if (accepted is null || world.Current is null || snapshots is not IColonySnapshotPackageStore packages) return;
        string owner = string.IsNullOrWhiteSpace(accepted.Identity.OwnerId) ? userId : accepted.Identity.OwnerId!;
        string colony = string.IsNullOrWhiteSpace(accepted.Identity.ColonyId) ? colonyId : accepted.Identity.ColonyId!;
        var package = packages.GetLatestPackage(owner, colony);
        if (package is null || package.Envelope.Identity.SnapshotId != accepted.Identity.SnapshotId || package.Payload.Length == 0) return;
        var extensions = plugins.ActiveWorldConfigurationExtensions(baseline.Current);
        IReadOnlyList<WorldTileFloatLayerProjection> projections;
        try
        {
            var bytes = SaveSnapshotPackageFileReader.DecodePayload(package.Payload, package.Envelope.PayloadEncoding);
            projections = extensions.ProjectConfirmOnAcceptedSnapshotLayersFromSave(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException or NotSupportedException)
        {
            // Optional source data may be unreadable; publication and acknowledgement failures must still propagate.
            logger().LogWarning(ex, "Failed to project world layers from accepted snapshot {SnapshotId}", accepted.Identity.SnapshotId);
            return;
        }
        if (projections.Count == 0) return;
        var actor = new EventParty(userId, colonyId, accepted.Identity.SnapshotId);
        foreach (var projection in projections)
        {
            world.ConfirmWorldTileFloatLayerWithNotifications(projection.LayerId, projection.Values, extensions,
                increases => new WorldLayerNotificationWork(1, Guid.NewGuid().ToString("N"), world.Current!.WorldConfigurationId,
                    actor, projection.LayerId, increases,
                    notifications.Build(world.Current!, extensions, actor, increases, acceptedAtUtc),
                    players.List().Select(player => player.UserId).Append(userId).Distinct(StringComparer.Ordinal).ToArray()));
        }
        notifications.Recover();
    }
}
