using static AIRsLight.ClashOfRim.Network.Modules.World.WorldTileGeometry;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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

    private static IResult AbandonPlayerColony(
        AbandonPlayerColonyRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("AbandonColony.MissingRequest")),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        // Reject unauthenticated requests before doing recovery work. Repeat this
        // check under the gate because a valid request may wait behind a deletion.
        if (!IsAuthorizedForColony(state, request.AuthToken, request.UserId, request.ColonyId,
                authorizationEventId: null, authorizationScope: null, nowUtc, out string earlyAuthFailure))
        {
            return Results.Ok(new AbandonPlayerColonyResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, earlyAuthFailure),
                removedSnapshots: 0,
                removedSites: 0,
                removedEvents: 0,
                removedRuntimeMarkers: 0,
                worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                worldMapMarkers: null));
        }

        AbandonedColonyCleanupResult cleanup;
        using (state.EnterSnapshotMutation())
        {
            RecoverSnapshotCommits(state);
            nowUtc = DateTimeOffset.UtcNow;
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
                return Results.Ok(new AbandonPlayerColonyResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                    removedSnapshots: 0,
                    removedSites: 0,
                    removedEvents: 0,
                    removedRuntimeMarkers: 0,
                    worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                    worldMapMarkers: null));
            }

            LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(request.UserId, request.ColonyId);
            if (!string.IsNullOrWhiteSpace(request.CurrentSnapshotId)
                && latest is not null
                && !string.Equals(latest.Identity.SnapshotId, request.CurrentSnapshotId, StringComparison.Ordinal))
            {
                return Results.Ok(new AbandonPlayerColonyResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("AbandonColony.SnapshotNotLatest")),
                    removedSnapshots: 0,
                    removedSites: 0,
                    removedEvents: 0,
                    removedRuntimeMarkers: 0,
                    worldConfiguration: state.WorldDelivery.Build(state.WorldConfiguration.Current),
                    worldMapMarkers: null));
            }

            cleanup = CleanupAbandonedColonyLocked(
                state,
                request.UserId,
                request.ColonyId,
                latest,
                nowUtc);
        }

        WorldConfigurationDto? deliveredConfiguration = state.WorldDelivery.Build(cleanup.RemovedSites.Session.WorldConfiguration);
        WorldMapMarkerDelivery markers = state.WorldMarkers.Build(request.UserId, request.ColonyId, nowUtc);
        RuntimeLogger(loggerFactory).LogInformation(
            "殖民地废弃完成：user={UserId} colony={ColonyId} snapshotRemoved={SnapshotRemoved} removedSites={RemovedSites} removedEvents={RemovedEvents} removedRuntimeMarkers={RemovedRuntimeMarkers}",
            request.UserId,
            request.ColonyId,
            cleanup.SnapshotRemoved,
            cleanup.RemovedSites.RemovedCount,
            cleanup.RemovedEvents,
            cleanup.RemovedRuntimeMarkers);
        return Results.Ok(new AbandonPlayerColonyResponse(
            ProtocolResponse.Ok(T("AbandonColony.Completed")),
            cleanup.SnapshotRemoved ? 1 : 0,
            cleanup.RemovedSites.RemovedCount,
            cleanup.RemovedEvents,
            cleanup.RemovedRuntimeMarkers,
            deliveredConfiguration,
            ProtocolDtoMapper.ToDto(markers)));
    }

    private static AbandonedColonyCleanupResult CleanupAbandonedColony(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc)
    {
        using (state.EnterSnapshotMutation())
        {
            RecoverSnapshotCommits(state);
            return CleanupAbandonedColonyLocked(state, userId, colonyId, state.SnapshotStore.GetLatest(userId, colonyId), nowUtc);
        }
    }

    private static AbandonedColonyCleanupResult CleanupAbandonedColonyLocked(
        ClashOfRimNetworkState state, string userId, string colonyId, LatestSnapshotRecord? latest, DateTimeOffset nowUtc)
    {
        if (state.SnapshotCommits.HasPending)
            throw new InvalidOperationException("Finish snapshot commits before beginning colony deletion.");
        LedgerBatch closure = BuildAbandonedColonyEventClosure(state.Ledger, userId, colonyId, out var notifyUsers);
        var existing = state.Ledger.FindByIdempotencyKey(ColonyTombstones.Key(userId, colonyId));
        if (existing is not null && latest is not null)
            throw new InvalidDataException("A deleted colony has a snapshot again; refusing to reuse its tombstone.");
        AuthoritativeEvent tombstone = existing ?? CreateColonyTombstoneEvent(userId, colonyId, latest?.Identity.SnapshotId, nowUtc);
        notifyUsers.Add(userId);
        var intent = new ColonyDeletionIntent(1, Guid.NewGuid().ToString("N"), userId, colonyId, latest?.Envelope,
            tombstone.CreatedAtUtc, closure with { Events = [tombstone] },
            closure.StatusChanges.Select(change => state.Ledger.Find(change.EventId)!).ToArray(), notifyUsers.ToArray(),
            state.Players.List().Select(player => player.UserId).Append(userId).Distinct(StringComparer.Ordinal).ToArray());
        intent.Preflight(state.SnapshotStore, state.Ledger);
        return state.ColonyDeletions.Commit(intent, pending => ApplyColonyDeletion(state, pending));
    }

    private static AbandonedColonyCleanupResult ApplyColonyDeletion(ClashOfRimNetworkState state, ColonyDeletionIntent intent)
    {
        intent.Preflight(state.SnapshotStore, state.Ledger);
        string userId = intent.UserId, colonyId = intent.ColonyId;
        bool snapshotRemoved = state.SnapshotStore.RemoveLatest(userId, colonyId);
        RemovePlayerColonySitesResult removedSites = state.WorldConfiguration.RemovePlayerColonySites(
            userId,
            colonyId,
            ActiveWorldConfigurationExtensions(state));
        int removedEvents = 0;
        int removedRuntimeMarkers = state.RuntimeWorldObjectMarkers.RemoveForOwner(userId, colonyId);
        state.DiplomacyRelations.RemoveForColony(userId, colonyId);
        state.BankLoans.RemoveForColony(userId, colonyId);
        state.Players.MarkDeleted(userId, colonyId, intent.Tombstone.AppliedSnapshotId, intent.DeletedAtUtc);
        state.RaidPreparations.RemoveForColony(userId, colonyId);
        state.RaidProtectionActivations.RemoveForColony(userId, colonyId);
        state.MercenaryContracts.CloseForColony(userId, colonyId);
        state.Ledger.ApplyBatch(intent.Batch);
        // Deletion and login publication share the mutation gate. Retire only this
        // colony's connection; recovery must never disconnect its replacement.
        state.SessionConnections.EndUnderMutation(userId, state.LoginSessions.FindSessionIdForColony(userId, colonyId));
        state.AuthTokens.RevokeForColony(userId, colonyId);
        state.EventNotifications.SignalUsers(intent.NotifyUsers);
        state.WorldConfigurationNotifications.SignalUsers(intent.WorldNotifyUsers.Concat(state.Players.List().Select(player => player.UserId)));

        return new AbandonedColonyCleanupResult(
            snapshotRemoved,
            removedSites,
            removedEvents,
            removedRuntimeMarkers);
    }

    private sealed record AbandonedColonyCleanupResult(
        bool SnapshotRemoved,
        RemovePlayerColonySitesResult RemovedSites,
        int RemovedEvents,
        int RemovedRuntimeMarkers);
}
