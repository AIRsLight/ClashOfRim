using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

// Persisted in the same world-configuration transaction as its layer increase.
internal sealed record WorldLayerNotificationWork(int Version, string Id, string WorldConfigurationId,
    EventParty Actor, string LayerId, IReadOnlyList<WorldTileFloatLayerIncrease> Increases,
    IReadOnlyList<AuthoritativeEvent> Notifications, IReadOnlyList<string> WorldNotifyUsers)
{
    internal void Validate()
    {
        if (Version != 1 || !Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(WorldConfigurationId)
            || string.IsNullOrWhiteSpace(LayerId) || Actor is null || string.IsNullOrWhiteSpace(Actor.UserId)
            || string.IsNullOrWhiteSpace(Actor.ColonyId) || string.IsNullOrWhiteSpace(Actor.FactionId)
            || Increases is null || Increases.Count == 0 || Notifications is null || WorldNotifyUsers is null
            || WorldNotifyUsers.Any(string.IsNullOrWhiteSpace) || !WorldNotifyUsers.Contains(Actor.UserId)
            || Increases.Any(item => item is null || item.LayerId != LayerId || item.Tile < 0
                || !float.IsFinite(item.PreviousValue) || !float.IsFinite(item.CurrentValue) || !float.IsFinite(item.Delta)
                || item.PreviousValue < 0 || item.CurrentValue > 1 || item.CurrentValue <= item.PreviousValue
                || Math.Abs(item.Delta - (item.CurrentValue - item.PreviousValue)) > 0.0001f))
            throw new InvalidOperationException("Invalid pending world-layer notification work.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Notifications)
            if (item is null || item.Type != ServerEventType.ServerNotification || item.Actor != Actor
                || item.Target is null || string.IsNullOrWhiteSpace(item.Target.UserId) || string.IsNullOrWhiteSpace(item.Target.ColonyId)
                || string.IsNullOrWhiteSpace(item.EventId) || string.IsNullOrWhiteSpace(item.IdempotencyKey)
                || !ids.Add(item.EventId) || !keys.Add(item.IdempotencyKey)
                || item.Payload is not ServerNotificationEventPayload payload || payload.NotificationId != item.IdempotencyKey)
                throw new InvalidOperationException("Invalid event in pending world-layer notification work.");
    }
}
