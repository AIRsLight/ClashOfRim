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

}
