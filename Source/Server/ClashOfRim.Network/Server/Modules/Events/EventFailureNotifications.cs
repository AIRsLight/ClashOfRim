using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal static class EventFailureNotifications
{
    internal static IReadOnlyList<AuthoritativeEvent> FindFailureNotifications(IAuthoritativeEventLedger ledger, AuthoritativeEvent root)
    {
        var found = ledger.ListAll().Where(item =>
            item.IdempotencyKey == $"event-application-failed:{root.EventId}:{item.Target.UserId}:{item.Target.ColonyId}"
            || item.Payload is ServerNotificationEventPayload { RelatedEventId: { } related } && related == root.EventId
                && item.IdempotencyKey.StartsWith("event-application-failed:", StringComparison.Ordinal)).ToArray();
        foreach (var item in found) ValidateFailureNotification(root, item);
        return found;
    }

    internal static void ValidateFailureNotification(AuthoritativeEvent root, AuthoritativeEvent item)
    {
        string key = $"event-application-failed:{root.EventId}:{item.Target.UserId}:{item.Target.ColonyId}";
        if (item.Type != ServerEventType.ServerNotification || item.Actor != new EventParty("server")
            || item.IdempotencyKey != key || item.Payload is not ServerNotificationEventPayload payload
            || payload.NotificationId != key || payload.FromAdministrator
            || payload.RelatedEventId is not null && payload.RelatedEventId != root.EventId
            || payload.RelatedEventType is not null && payload.RelatedEventType != root.Type)
            throw new InvalidOperationException("Failure notification conflicts with its source.");
    }

    internal static IEnumerable<AuthoritativeEvent> Create(
        IAuthoritativeEventLedger ledger, OnlinePresenceRegistry presence,
        AuthoritativeEvent rootEvent,
        IReadOnlyCollection<AuthoritativeEvent> affected,
        string reason,
        DateTimeOffset nowUtc)
    {
        IReadOnlyList<EventParty> targets = affected
            .SelectMany(evt => new[] { evt.Actor, evt.Target })
            .Where(target => !string.IsNullOrWhiteSpace(target.UserId)
                && !string.Equals(target.UserId, "server", StringComparison.OrdinalIgnoreCase))
            .GroupBy(target => target.UserId + "\n" + target.ColonyId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        foreach (EventParty target in targets)
        {
            string notificationId = $"event-application-failed:{rootEvent.EventId}:{target.UserId}:{target.ColonyId}";
            if (ledger.FindByIdempotencyKey(notificationId) is { } existing)
            {
                ValidateFailureNotification(rootEvent, existing);
                yield return existing;
                continue;
            }
            AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(ledger,
                ServerEventType.ServerNotification,
                new EventParty("server"),
                target,
                notificationId,
                presence.IsUserOnline(target.UserId),
                new ServerNotificationEventPayload(
                    notificationId,
                    ServerText.Format("Events.ApplicationFailedTitle"),
                    ServerText.Format("Events.ApplicationFailedMessage", ("EVENT", rootEvent.EventId), ("REASON", reason)),
                    ServerNotificationSeverity.Warning,
                    FromAdministrator: false, RelatedEventId: rootEvent.EventId, RelatedEventType: rootEvent.Type),
                nowUtc,
                rootEvent.TargetContext);
            yield return notification;
        }
    }
}
