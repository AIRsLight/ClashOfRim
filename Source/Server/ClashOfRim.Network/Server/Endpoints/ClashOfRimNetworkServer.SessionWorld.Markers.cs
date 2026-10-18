using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static bool HasPendingOldMapBoundDeliveryEvent(
        ClashOfRimNetworkState state,
        string userId,
        string? colonyId,
        PlayerColonySiteDto oldSite)
    {
        return state.Ledger.ListForUser(userId)
            .Any(ledgerEvent =>
                IsRelocationBlockingEventForTarget(ledgerEvent, userId, colonyId)
                && IsActiveRelocationBlockingEvent(ledgerEvent.Status)
                && IsTargetContextBoundToColonySite(ledgerEvent.TargetContext, oldSite));
    }

    private static bool IsRelocationBlockingEventForTarget(
        AuthoritativeEvent ledgerEvent,
        string userId,
        string? colonyId)
    {
        if (!RaidMarkerProjection.MatchesParty(ledgerEvent.Target, userId, colonyId))
        {
            return false;
        }

        return ledgerEvent.Type switch
        {
            ServerEventType.ItemDelivery => true,
            ServerEventType.SupportPawn => true,
            ServerEventType.Trade => ledgerEvent.Payload is TradeEventPayload tradePayload
                && tradePayload.Stage is not TradeStage.MarketOrder
                && tradePayload.Stage is not TradeStage.AcceptedMemo,
            _ => false
        };
    }

    private static bool IsActiveRelocationBlockingEvent(ServerEventStatus status)
    {
        return status is ServerEventStatus.Recorded
            or ServerEventStatus.ReadyForImmediateDelivery
            or ServerEventStatus.PendingOfflineDelivery
            or ServerEventStatus.DeliveredToClient
            or ServerEventStatus.Conflict;
    }

    private static bool IsTargetContextBoundToColonySite(EventTargetContext? targetContext, PlayerColonySiteDto site)
    {
        if (targetContext is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(site.WorldObjectId)
            && !string.IsNullOrWhiteSpace(targetContext.WorldObjectId)
            && string.Equals(targetContext.WorldObjectId, site.WorldObjectId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(site.MapUniqueId)
            && !string.IsNullOrWhiteSpace(targetContext.MapUniqueId)
            && string.Equals(targetContext.MapUniqueId, site.MapUniqueId, StringComparison.Ordinal))
        {
            return true;
        }

        return site.Tile >= 0 && targetContext.Tile == site.Tile;
    }

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
