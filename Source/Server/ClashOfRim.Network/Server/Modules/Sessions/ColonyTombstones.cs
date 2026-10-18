using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal static class ColonyTombstones
{
    internal static string Key(string userId, string colonyId) => "colony-abandoned:" + userId + ":" + colonyId;
    internal static string NotificationId(string userId, string colonyId) => "colony-tombstone:" + userId + ":" + colonyId;

    internal static bool IsFor(AuthoritativeEvent item, string userId, string colonyId) =>
        item.Type == ServerEventType.ServerNotification && item.Payload is ServerNotificationEventPayload payload
        && payload.NotificationId == NotificationId(userId, colonyId);

    internal static bool IsDeleted(IAuthoritativeEventLedger ledger, EventParty party) =>
        !string.IsNullOrWhiteSpace(party.ColonyId)
        && ledger.FindByIdempotencyKey(Key(party.UserId, party.ColonyId)) is { } tombstone
        && tombstone.Actor.UserId == party.UserId && tombstone.Actor.ColonyId == party.ColonyId
        && tombstone.Target.UserId == "server" && tombstone.Status == ServerEventStatus.AppliedToSnapshot
        && IsFor(tombstone, party.UserId, party.ColonyId);

    internal static AuthoritativeEvent Create(
        string userId,
        string colonyId,
        string? abandonedSnapshotId,
        DateTimeOffset nowUtc)
    {
        string notificationId = ColonyTombstones.NotificationId(userId, colonyId);
        string idempotencyKey = ColonyTombstones.Key(userId, colonyId);
        AuthoritativeEvent tombstone = new(
            AuthoritativeEventFactory.BuildEventId(ServerEventType.ServerNotification, idempotencyKey),
            ServerEventType.ServerNotification,
            new EventParty(userId, colonyId),
            new EventParty("server"),
            nowUtc,
            ServerEventStatus.AppliedToSnapshot,
            ServerEventDeliveryMode.OfflinePending,
            idempotencyKey,
            new ServerNotificationEventPayload(
                notificationId,
                "Colony tombstone",
                "Colony instance was abandoned.",
                ServerNotificationSeverity.Info,
                FromAdministrator: false,
                OnlineOnly: true,
                RelatedUserId: userId,
                RelatedColonyId: colonyId),
            null,
            EventRejectionPolicy.NotRejectable,
            TargetEventDecision.None,
            DecisionAtUtc: null,
            DecisionReason: null,
            EventApplicationResultKind.None,
            LastFailureReason: null,
            NextRetryAtUtc: null,
            DeliveredToSnapshotId: null,
            DeliveredAtUtc: null,
            AppliedSnapshotId: abandonedSnapshotId,
            AppliedAtUtc: nowUtc);
        return tombstone;
    }
}
