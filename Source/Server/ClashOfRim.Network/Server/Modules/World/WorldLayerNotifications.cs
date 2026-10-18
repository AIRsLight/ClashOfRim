using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.Modules.World.WorldTileGeometry;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldLayerNotifications(WorldConfigurationRegistry world, IAuthoritativeEventLedger ledger,
    PlayerRegistry players, OnlinePresenceRegistry presence, IWorldTileDistanceCalculator distances,
    SnapshotMutationCoordinator mutations, EventNotificationHub notifications, WorldConfigurationChanges worldChanges)
{
    internal void Recover()
    {
        using var mutation = mutations.EnterUnrecovered();
        foreach (var work in world.ListPendingLayerNotifications())
        {
            ledger.ApplyBatch(new LedgerBatch(work.Notifications, []));
            notifications.SignalUsers(work.Notifications.Select(item => item.Target.UserId));
            worldChanges.Signal(work.WorldNotifyUsers.ToArray());
            world.CompleteLayerNotifications(work.Id);
        }
    }

    internal IReadOnlyList<AuthoritativeEvent> Build(WorldConfigurationDto configuration, WorldConfigurationExtensionService extensions,
        EventParty actor, IReadOnlyList<WorldTileFloatLayerIncrease> increases, DateTimeOffset nowUtc)
    {
        var geometry = BuildWorldTileGeometryDistanceSource(configuration);
        var surface = configuration.TileGeometry?.Layers.FirstOrDefault(layer => string.Equals(layer.LayerDefName, "Surface", StringComparison.OrdinalIgnoreCase));
        int layerId = surface?.LayerId ?? configuration.TileGeometry?.Layers.OrderBy(layer => layer.LayerId).FirstOrDefault()?.LayerId ?? 0;
        var player = players.FindByUserId(actor.UserId);
        string label = player is not null && player.ColonyId == actor.ColonyId && !string.IsNullOrWhiteSpace(player.DisplayName) ? player.DisplayName! : actor.UserId;
        var context = new WorldTileFloatLayerNotificationContext(label);
        var events = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
        foreach (var increase in increases)
        {
            var notification = extensions.BuildIncreaseNotification(context, increase);
            if (notification is null || notification.RadiusTiles < 0) continue;
            foreach (var site in configuration.PlayerColonySites)
            {
                if (site.Tile < 0 || site.UserId == actor.UserId || string.IsNullOrWhiteSpace(site.UserId) || string.IsNullOrWhiteSpace(site.ColonyId)) continue;
                // Float-layer notifications retain their surface-distance semantics across colony layers.
                int? distance = distances.TryCalculateDistance(geometry, new(increase.Tile, layerId), new(site.Tile, layerId), crossLayerOverheadDistanceTiles: 0);
                if (distance is null || distance > notification.RadiusTiles) continue;
                string key = $"{notification.Kind}:{actor.UserId}:{actor.ColonyId}:{actor.FactionId}:{increase.LayerId}:{increase.Tile}:{site.UserId}:{site.ColonyId}";
                var item = AuthoritativeEventFactory.CreateForLedger(ledger, ServerEventType.ServerNotification, actor,
                    new(site.UserId, site.ColonyId), key, presence.IsUserOnline(site.UserId),
                    new ServerNotificationEventPayload(key, notification.Title, notification.Message, notification.Severity, FromAdministrator: false), nowUtc,
                    new(site.WorldObjectId, site.MapUniqueId, site.Tile, EventLandingMode.MapEdge));
                events.TryAdd(key, item);
            }
        }
        return events.Values.ToArray();
    }
}
