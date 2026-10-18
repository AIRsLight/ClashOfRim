using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult SyncWorldMapMarkers(SyncWorldMapMarkersRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        state.RaidTimeouts.Reconcile(nowUtc);
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Results.Ok(new WorldMapMarkerDeliveryDto(
                string.Empty,
                nowUtc,
                Array.Empty<WorldMapMarkerDto>()));
        }

        WorldMapMarkerDelivery delivery = state.WorldMarkers.Build(request.UserId, request.ColonyId, nowUtc);

        return Results.Ok(ProtocolDtoMapper.ToDto(delivery));
    }

    private static IResult SyncRuntimeWorldObjects(SyncRuntimeWorldObjectsRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
        {
            return Results.Ok(new SyncRuntimeWorldObjectsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("RuntimeObjects.MissingIdentity")),
                request.UserId,
                nowUtc,
                acceptedCount: 0,
                worldMapMarkers: null));
        }

        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new SyncRuntimeWorldObjectsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                request.UserId,
                nowUtc,
                acceptedCount: 0,
                worldMapMarkers: null));
        }

        int accepted = state.RuntimeWorldObjectMarkers.ReplaceForOwner(
            request.UserId,
            request.ColonyId,
            request.SnapshotId,
            request.Objects,
            nowUtc);
        WorldMapMarkerDelivery delivery = state.WorldMarkers.Build(request.UserId, request.ColonyId, nowUtc);
        return Results.Ok(new SyncRuntimeWorldObjectsResponse(
            ProtocolResponse.Ok(T("RuntimeObjects.Synced")),
            request.UserId,
            nowUtc,
            accepted,
            ProtocolDtoMapper.ToDto(delivery)));
    }

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

    private static bool HasRegisteredPlayerColonySite(
        WorldConfigurationDto? configuration,
        string userId,
        string colonyId)
    {
        return configuration?.PlayerColonySites.Any(site =>
            string.Equals(site.UserId, userId, StringComparison.Ordinal)
            && string.Equals(site.ColonyId, colonyId, StringComparison.Ordinal)
            && site.Tile >= 0) == true;
    }

    private static string? FindExistingColonyIdForUser(
        ClashOfRimNetworkState state,
        WorldConfigurationDto? configuration,
        string userId,
        string requestedColonyId)
    {
        LatestSnapshotRecord? snapshotCandidate = null;
        foreach (LatestSnapshotRecord snapshot in state.SnapshotStore.ListLatest())
        {
            if (!string.Equals(snapshot.Identity.OwnerId, userId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId)
                || state.Players.IsDeleted(userId, snapshot.Identity.ColonyId!))
            {
                continue;
            }

            if (snapshotCandidate is null || snapshot.AcceptedAtUtc > snapshotCandidate.AcceptedAtUtc)
            {
                snapshotCandidate = snapshot;
            }
        }

        if (snapshotCandidate is not null)
        {
            return snapshotCandidate.Identity.ColonyId;
        }

        string? fallbackColonyId = null;
        if (configuration is null)
        {
            return fallbackColonyId;
        }

        foreach (PlayerColonySiteDto site in configuration.PlayerColonySites)
        {
            if (!string.Equals(site.UserId, userId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(site.ColonyId)
                || site.Tile < 0
                || state.Players.IsDeleted(userId, site.ColonyId!))
            {
                continue;
            }

            if (string.Equals(site.ColonyId, requestedColonyId, StringComparison.Ordinal))
            {
                return site.ColonyId;
            }

            if (fallbackColonyId is null
                || string.Compare(site.ColonyId, fallbackColonyId, StringComparison.Ordinal) < 0)
            {
                fallbackColonyId = site.ColonyId;
            }
        }

        return fallbackColonyId;
    }

    private static string CreateDefaultColonyId(string userId)
    {
        string normalized = new(userId
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = Guid.NewGuid().ToString("N")[..8];
        }

        return "colony-" + normalized;
    }

    private static bool HasHistoricalColonyLedgerReference(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId)
    {
        return state.Ledger.ListByTypeForActor(ServerEventType.ServerNotification, userId, colonyId).Any(ledgerEvent =>
            IsVisibleParty(ledgerEvent.Actor, userId, colonyId)
            && ColonyTombstones.IsFor(ledgerEvent, userId, colonyId));
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
