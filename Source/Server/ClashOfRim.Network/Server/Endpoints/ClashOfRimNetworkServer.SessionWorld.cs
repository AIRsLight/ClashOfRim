using static AIRsLight.ClashOfRim.Network.Modules.World.WorldTileGeometry;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static WorldConfigurationExtensionService ActiveWorldConfigurationExtensions(ClashOfRimNetworkState state)
    {
        return state.Plugins.ActiveWorldConfigurationExtensions(state.CompatibilityBaseline.Current);
    }

    private static IReadOnlyList<AuthoritativeEvent> BuildWorldTileFloatLayerIncreaseNotifications(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string snapshotId,
        IReadOnlyList<WorldTileFloatLayerIncrease> increases,
        DateTimeOffset nowUtc)
    {
        if (increases.Count == 0 || state.WorldConfiguration.Current is not WorldConfigurationDto configuration)
        {
            return [];
        }

        WorldTileGeometryDistanceSource? geometry = BuildWorldTileGeometryDistanceSource(configuration);
        int surfaceLayerId = ResolveSurfaceLayerId(configuration);
        EventParty actor = new(userId, colonyId, snapshotId);
        string actorLabel = ResolvePlayerDisplayName(state, userId, colonyId);
        var notificationContext = new WorldTileFloatLayerNotificationContext(actorLabel);
        var notifications = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
        foreach (WorldTileFloatLayerIncrease increase in increases)
        {
            WorldTileFloatLayerIncreaseNotification? notification =
                ActiveWorldConfigurationExtensions(state).BuildIncreaseNotification(notificationContext, increase);
            if (notification is null || notification.RadiusTiles < 0)
            {
                continue;
            }

            foreach (PlayerColonySiteDto site in configuration.PlayerColonySites)
            {
                if (site.Tile < 0
                    || string.Equals(site.UserId, userId, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(site.UserId)
                    || string.IsNullOrWhiteSpace(site.ColonyId))
                {
                    continue;
                }

                int? distance = state.WorldTileDistanceCalculator.TryCalculateDistance(
                    geometry,
                    new WorldTileRef(increase.Tile, surfaceLayerId),
                    new WorldTileRef(site.Tile, surfaceLayerId),
                    crossLayerOverheadDistanceTiles: 0);
                if (distance is null || distance > notification.RadiusTiles)
                {
                    continue;
                }

                string notificationId = $"{notification.Kind}:{userId}:{colonyId}:{snapshotId}:{increase.LayerId}:{increase.Tile}:{site.UserId}:{site.ColonyId}";
                AuthoritativeEvent worldLayerNotification = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
                    ServerEventType.ServerNotification,
                    actor,
                    new EventParty(site.UserId, site.ColonyId),
                    notificationId,
                    state.OnlinePresence.IsUserOnline(site.UserId),
                    new ServerNotificationEventPayload(
                        notificationId,
                        notification.Title,
                        notification.Message,
                        notification.Severity,
                        FromAdministrator: false),
                    nowUtc,
                    new EventTargetContext(site.WorldObjectId, site.MapUniqueId, site.Tile, EventLandingMode.MapEdge));
                notifications.TryAdd(notificationId, worldLayerNotification);
            }
        }

        return notifications.Values.ToArray();
    }

    private static int ResolveSurfaceLayerId(WorldConfigurationDto configuration)
    {
        WorldTileLayerGeometryDto? surface = configuration.TileGeometry?.Layers.FirstOrDefault(layer =>
            string.Equals(layer.LayerDefName, "Surface", StringComparison.OrdinalIgnoreCase));
        if (surface is not null)
        {
            return surface.LayerId;
        }

        return configuration.TileGeometry?.Layers.OrderBy(layer => layer.LayerId).FirstOrDefault()?.LayerId ?? 0;
    }

    private static string ResolvePlayerDisplayName(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId)
    {
        PlayerSessionRecord? player = state.Players.FindByUserId(userId);
        if (player is not null && !string.Equals(player.ColonyId, colonyId, StringComparison.Ordinal))
        {
            player = null;
        }

        return string.IsNullOrWhiteSpace(player?.DisplayName) ? userId : player!.DisplayName!;
    }

}
