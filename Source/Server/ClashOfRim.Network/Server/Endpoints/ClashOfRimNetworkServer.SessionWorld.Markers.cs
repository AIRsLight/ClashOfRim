using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static AuthoritativeEvent CreateColonyTombstoneEvent(
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
