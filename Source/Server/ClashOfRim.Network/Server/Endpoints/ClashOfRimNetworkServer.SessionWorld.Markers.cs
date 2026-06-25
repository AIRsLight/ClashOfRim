using AIRsLight.ClashOfRim.Compatibility;
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
        ReconcileExpiredRaidEvents(state, nowUtc);
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Results.Ok(new WorldMapMarkerDeliveryDto(
                string.Empty,
                nowUtc,
                Array.Empty<WorldMapMarkerDto>()));
        }

        WorldMapMarkerDelivery delivery = BuildWorldMapMarkerDelivery(
            request.UserId,
            request.ColonyId,
            state,
            nowUtc);

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
        WorldMapMarkerDelivery delivery = BuildWorldMapMarkerDelivery(request.UserId, request.ColonyId, state, nowUtc);
        return Results.Ok(new SyncRuntimeWorldObjectsResponse(
            ProtocolResponse.Ok(T("RuntimeObjects.Synced")),
            request.UserId,
            nowUtc,
            accepted,
            ProtocolDtoMapper.ToDto(delivery)));
    }

    private static WorldMapMarkerDelivery BuildWorldMapMarkerDelivery(
        string userId,
        string? colonyId,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc)
    {
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId = BuildPlayerLookupByUserId(state);
        bool lockedToActiveRaid = HasActiveRaidForActor(state, userId, colonyId);
        WorldMapMarkerDelivery delivery = new(
            userId,
            nowUtc,
            BuildActiveRaidTargetMarkersForDelivery(
                state,
                nowUtc,
                lockedToActiveRaid ? userId : null,
                lockedToActiveRaid ? colonyId : null,
                userId,
                colonyId),
            state.ServerConfiguration.GiftsEnabled,
            state.ServerConfiguration.PvpEnabled);
        if (lockedToActiveRaid)
        {
            IReadOnlyList<WorldMapMarker> raidOnlyMarkers = ContextualizeAndSortMarkers(
                userId,
                colonyId,
                delivery.Markers,
                state,
                playersByUserId);
            return delivery with { Markers = raidOnlyMarkers };
        }

        WorldConfigurationDto? deliveredConfiguration = BuildWorldConfigurationForDelivery(state.WorldConfiguration.Current, state);
        IReadOnlyList<WorldMapMarker> baselineMarkers = BuildConfiguredPlayerColonyMarkers(
            deliveredConfiguration,
            nowUtc);
        baselineMarkers = WorldMapRaidAvailabilityAttacher.Attach(
            userId,
            baselineMarkers,
            BuildRaidAvailabilitySources(userId, colonyId, baselineMarkers, state, nowUtc),
            nowUtc,
            BuildRaidEligibilityPolicy(state.ServerConfiguration));
        baselineMarkers = WithFriendlyReinforcementAvailability(userId, colonyId, baselineMarkers, state);
        IReadOnlyList<WorldMapMarker> runtimeMarkers = state.RuntimeWorldObjectMarkers.ListVisibleMarkers(userId, nowUtc);
        IReadOnlyList<WorldMapMarker> markers = MergeContextualizeAndSortMarkers(
            userId,
            colonyId,
            delivery.Markers,
            baselineMarkers,
            runtimeMarkers,
            state,
            playersByUserId);
        return delivery with { Markers = markers };
    }

    private static IReadOnlyList<WorldMapMarker> ContextualizeAndSortMarkers(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> markers,
        ClashOfRimNetworkState state,
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId)
    {
        var result = new List<WorldMapMarker>(markers.Count);
        foreach (WorldMapMarker marker in markers)
        {
            result.Add(WithViewerMarkerContext(viewerUserId, viewerColonyId, marker, state, playersByUserId));
        }

        SortWorldMapMarkers(result);
        return result;
    }

    private static IReadOnlyList<WorldMapMarker> MergeContextualizeAndSortMarkers(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> raidMarkers,
        IReadOnlyList<WorldMapMarker> baselineMarkers,
        IReadOnlyList<WorldMapMarker> runtimeMarkers,
        ClashOfRimNetworkState state,
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId)
    {
        int capacity = raidMarkers.Count + baselineMarkers.Count + runtimeMarkers.Count;
        var seenMarkerIds = new HashSet<string>(capacity, StringComparer.Ordinal);
        var result = new List<WorldMapMarker>(capacity);
        AddWorldMapMarkers(viewerUserId, viewerColonyId, raidMarkers, state, playersByUserId, seenMarkerIds, result);
        AddWorldMapMarkers(viewerUserId, viewerColonyId, baselineMarkers, state, playersByUserId, seenMarkerIds, result);
        AddWorldMapMarkers(viewerUserId, viewerColonyId, runtimeMarkers, state, playersByUserId, seenMarkerIds, result);
        SortWorldMapMarkers(result);
        return result;
    }

    private static void AddWorldMapMarkers(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> markers,
        ClashOfRimNetworkState state,
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId,
        HashSet<string> seenMarkerIds,
        List<WorldMapMarker> result)
    {
        foreach (WorldMapMarker marker in markers)
        {
            if (!seenMarkerIds.Add(marker.MarkerId))
            {
                continue;
            }

            result.Add(WithViewerMarkerContext(viewerUserId, viewerColonyId, marker, state, playersByUserId));
        }
    }

    private static void SortWorldMapMarkers(List<WorldMapMarker> markers)
    {
        markers.Sort(CompareWorldMapMarkers);
    }

    private static int CompareWorldMapMarkers(WorldMapMarker left, WorldMapMarker right)
    {
        int tileComparison = left.Tile.CompareTo(right.Tile);
        if (tileComparison != 0)
        {
            return tileComparison;
        }

        int kindComparison = left.Kind.CompareTo(right.Kind);
        return kindComparison != 0
            ? kindComparison
            : string.Compare(left.MarkerId, right.MarkerId, StringComparison.Ordinal);
    }

    private static WorldMapMarker WithViewerMarkerContext(
        string viewerUserId,
        string? viewerColonyId,
        WorldMapMarker marker,
        ClashOfRimNetworkState state,
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId)
    {
        string ownerUserId = marker.OwnerUserId ?? string.Empty;
        playersByUserId.TryGetValue(ownerUserId, out PlayerSessionRecord? owner);
        string relationKind = ResolveDiplomacyRelationKind(
            state,
            viewerUserId,
            viewerColonyId,
            ownerUserId,
            marker.ColonyId);
        return marker with
        {
            RelationKind = relationKind,
            OwnerOnline = state.OnlinePresence.IsUserOnline(ownerUserId),
            OwnerLastSeenAtUtc = owner?.LastSeenAtUtc
        };
    }

    private static IReadOnlyDictionary<string, PlayerSessionRecord> BuildPlayerLookupByUserId(ClashOfRimNetworkState state)
    {
        IReadOnlyList<PlayerSessionRecord> players = state.Players.List();
        var result = new Dictionary<string, PlayerSessionRecord>(players.Count, StringComparer.Ordinal);
        foreach (PlayerSessionRecord player in players)
        {
            if (!string.IsNullOrWhiteSpace(player.UserId))
            {
                result[player.UserId] = player;
            }
        }

        return result;
    }

    private static WorldMapMarker WithFriendlyReinforcementAvailability(
        string viewerUserId,
        string? viewerColonyId,
        WorldMapMarker marker,
        ClashOfRimNetworkState state)
    {
        if (marker.Kind != WorldMapMarkerKind.TradeableColony
            || string.IsNullOrWhiteSpace(marker.OwnerUserId)
            || string.Equals(marker.OwnerUserId, viewerUserId, StringComparison.Ordinal)
            || !AreAllied(state, viewerUserId, viewerColonyId, marker.OwnerUserId, marker.ColonyId)
            || string.IsNullOrWhiteSpace(marker.MapUniqueId))
        {
            return marker;
        }

        LatestSnapshotRecord? snapshot = string.IsNullOrWhiteSpace(marker.ColonyId)
            ? null
            : state.SnapshotStore.GetLatest(marker.OwnerUserId, marker.ColonyId!);
        string? snapshotId = marker.SnapshotId
            ?? marker.RaidAvailability?.DefenderSnapshotId
            ?? snapshot?.Identity.SnapshotId;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return marker;
        }

        return marker with
        {
            SnapshotId = snapshotId,
            ReinforcementEnabled = true
        };
    }

    private static IReadOnlyList<WorldMapMarker> WithFriendlyReinforcementAvailability(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> markers,
        ClashOfRimNetworkState state)
    {
        var result = new List<WorldMapMarker>(markers.Count);
        foreach (WorldMapMarker marker in markers)
        {
            result.Add(WithFriendlyReinforcementAvailability(viewerUserId, viewerColonyId, marker, state));
        }

        return result;
    }

    private static IReadOnlyList<WorldMapMarker> BuildActiveRaidTargetMarkersForDelivery(
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc,
        string? actorUserId = null,
        string? actorColonyId = null,
        string? viewerUserId = null,
        string? viewerColonyId = null)
    {
        IReadOnlyList<AuthoritativeEvent> raidEvents = string.IsNullOrWhiteSpace(actorUserId)
            ? state.Ledger.ListByType(ServerEventType.Raid)
            : state.Ledger.ListByTypeForActor(ServerEventType.Raid, actorUserId!, actorColonyId);

        var markers = new List<WorldMapMarker>();
        foreach (AuthoritativeEvent ledgerEvent in raidEvents)
        {
            if (!IsActiveRaidMarkerEvent(ledgerEvent)
                || (!string.IsNullOrWhiteSpace(actorUserId) && !IsEventActor(ledgerEvent, actorUserId, actorColonyId))
                || (!string.IsNullOrWhiteSpace(viewerUserId) && IsEventTarget(ledgerEvent, viewerUserId, viewerColonyId))
                || ledgerEvent.TargetContext?.Tile is null)
            {
                continue;
            }

            WorldMapMarker? marker = BuildActiveRaidTargetMarker(state, ledgerEvent, nowUtc);
            if (marker is not null)
            {
                markers.Add(marker);
            }
        }

        return markers;
    }

    private static bool HasActiveRaidForActor(
        ClashOfRimNetworkState state,
        string userId,
        string? colonyId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        foreach (AuthoritativeEvent ledgerEvent in state.Ledger.ListByTypeForActor(ServerEventType.Raid, userId, colonyId))
        {
            if (IsActiveRaidMarkerEvent(ledgerEvent) && IsEventActor(ledgerEvent, userId, colonyId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasActiveRaidForParticipant(
        ClashOfRimNetworkState state,
        string userId,
        string? colonyId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        if (HasActiveRaidForParticipant(
                state.Ledger.ListByTypeForActor(ServerEventType.Raid, userId, colonyId),
                userId,
                colonyId))
        {
            return true;
        }

        return HasActiveRaidForParticipant(
            state.Ledger.ListByTypeForTarget(ServerEventType.Raid, userId, colonyId),
            userId,
            colonyId);
    }

    private static bool HasActiveRaidForParticipant(
        IReadOnlyList<AuthoritativeEvent> events,
        string userId,
        string? colonyId)
    {
        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (!IsActiveRaidMarkerEvent(ledgerEvent))
            {
                continue;
            }

            if (IsEventActor(ledgerEvent, userId, colonyId)
                || IsEventTarget(ledgerEvent, userId, colonyId))
            {
                return true;
            }
        }

        return false;
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
        if (!IsEventTarget(ledgerEvent, userId, colonyId))
        {
            return false;
        }

        return ledgerEvent.Type switch
        {
            ServerEventType.Gift or ServerEventType.GiftReturn => true,
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

    private static bool IsEventActor(
        AuthoritativeEvent ledgerEvent,
        string userId,
        string? colonyId)
    {
        if (!string.Equals(ledgerEvent.Actor.UserId, userId, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(colonyId)
            || string.Equals(ledgerEvent.Actor.ColonyId, colonyId, StringComparison.Ordinal);
    }

    private static bool IsEventTarget(
        AuthoritativeEvent ledgerEvent,
        string userId,
        string? colonyId)
    {
        if (!string.Equals(ledgerEvent.Target.UserId, userId, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(colonyId)
            || string.Equals(ledgerEvent.Target.ColonyId, colonyId, StringComparison.Ordinal);
    }

    private static WorldMapMarker? BuildActiveRaidTargetMarker(
        ClashOfRimNetworkState state,
        AuthoritativeEvent ledgerEvent,
        DateTimeOffset nowUtc)
    {
        if (ledgerEvent.Payload is not RaidEventPayload payload || ledgerEvent.TargetContext?.Tile is null)
        {
            return null;
        }

        LatestSnapshotRecord? attackerSnapshot = string.IsNullOrWhiteSpace(ledgerEvent.Actor.ColonyId)
            ? null
            : state.SnapshotStore.GetLatest(
                ledgerEvent.Actor.UserId,
                ledgerEvent.Actor.ColonyId!);
        MapSummary? attackerBattleMap = attackerSnapshot is null
            ? null
            : FindRemoteSessionMap(attackerSnapshot.Index);
        string? snapshotId = attackerSnapshot?.Identity.SnapshotId ?? payload.AttackForce?.AttackerSnapshotId;
        string? mapId = attackerBattleMap?.UniqueId ?? ledgerEvent.TargetContext.MapUniqueId;

        return new WorldMapMarker(
            $"active-raid:{ledgerEvent.EventId}",
            WorldMapMarkerKind.ActiveRaidTarget,
            ledgerEvent.Actor.UserId,
            ledgerEvent.Actor.ColonyId,
            attackerBattleMap?.ParentWorldObjectId ?? ledgerEvent.TargetContext.WorldObjectId,
            mapId,
            snapshotId,
            ledgerEvent.TargetContext.Tile.Value,
            T("WorldMap.AttackingPlayerLabel"),
            nowUtc,
            ledgerEvent.EventId,
            TradeEnabled: false,
            ReinforcementEnabled: true);
    }

    private static MapSummary? FindRemoteSessionMap(SaveSnapshotIndex index)
    {
        HashSet<string> remoteSessionWorldObjectIds = index.WorldObjects
            .Where(IsRemoteSessionWorldObject)
            .SelectMany(worldObject => new[] { worldObject.UniqueLoadId, worldObject.Id })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

        return index.Maps
            .Where(map => !string.IsNullOrWhiteSpace(map.UniqueId))
            .FirstOrDefault(map => !string.IsNullOrWhiteSpace(map.ParentWorldObjectId)
                && remoteSessionWorldObjectIds.Contains(map.ParentWorldObjectId!));
    }

    private static bool IsRemoteSessionWorldObject(WorldObjectSummary worldObject)
    {
        return IsRemoteSessionWorldObjectDef(worldObject.Def)
            || (!string.IsNullOrWhiteSpace(worldObject.Class)
                && worldObject.Class.IndexOf("RemoteSessionMapParent", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsRemoteSessionWorldObjectDef(string? defName)
    {
        return string.Equals(defName, "ClashOfRim_RemoteSessionMapParent", StringComparison.Ordinal)
            || string.Equals(defName, "ClashOfRim_RemoteScoutMapParent", StringComparison.Ordinal)
            || string.Equals(defName, "ClashOfRim_RemoteRaidObservationMapParent", StringComparison.Ordinal)
            || string.Equals(defName, "ClashOfRim_RemoteRaidBattleMapParent", StringComparison.Ordinal);
    }

    private static bool IsActiveRaidMarkerEvent(AuthoritativeEvent ledgerEvent)
    {
        if (ledgerEvent.Payload is not RaidEventPayload payload)
        {
            return false;
        }

        if (payload.AttackForce == null ||
            payload.AttackerLoss != null ||
            payload.Settlement != null ||
            payload.ReturnedSnapshotId != null)
        {
            return false;
        }

        if (payload.OpponentKind != RaidOpponentKind.Player)
        {
            return false;
        }

        if (ledgerEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return false;
        }

        return ledgerEvent.Status is ServerEventStatus.Recorded
            or ServerEventStatus.ReadyForImmediateDelivery
            or ServerEventStatus.PendingOfflineDelivery
            or ServerEventStatus.DeliveredToClient
            or ServerEventStatus.Conflict;
    }

    private static IReadOnlyList<WorldMapRaidAvailabilitySource> BuildRaidAvailabilitySources(
        string attackerUserId,
        string? attackerColonyId,
        IEnumerable<WorldMapMarker> markers,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(attackerUserId))
        {
            return Array.Empty<WorldMapRaidAvailabilitySource>();
        }

        var sources = new List<WorldMapRaidAvailabilitySource>();
        foreach (WorldMapMarker marker in markers)
        {
            if (marker.Kind != WorldMapMarkerKind.TradeableColony
                || string.IsNullOrWhiteSpace(marker.OwnerUserId)
                || string.IsNullOrWhiteSpace(marker.ColonyId))
            {
                continue;
            }

            LatestSnapshotRecord? snapshot = state.SnapshotStore.GetLatest(marker.OwnerUserId!, marker.ColonyId!);
            if (snapshot is null)
            {
                continue;
            }

            string relationKind = state.DiplomacyRelations.GetRelationKind(
                attackerUserId,
                attackerColonyId,
                marker.OwnerUserId,
                marker.ColonyId);
            int defenderWealth = ResolveRaidAvailabilityDefenderWealth(
                state,
                marker.OwnerUserId!,
                marker.ColonyId!,
                snapshot,
                nowUtc);
            sources.Add(new WorldMapRaidAvailabilitySource(
                snapshot.Identity,
                snapshot.Index.Maps,
                string.Equals(relationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal),
                state.OnlinePresence.IsUserOnline(marker.OwnerUserId),
                defenderWealth,
                CurrentRaidCooldownUntil(state, marker.OwnerUserId, marker.ColonyId, nowUtc)));
        }

        return sources;
    }

    private static int ResolveRaidAvailabilityDefenderWealth(
        ClashOfRimNetworkState state,
        string ownerUserId,
        string colonyId,
        LatestSnapshotRecord snapshot,
        DateTimeOffset nowUtc)
    {
        string snapshotOwnerId = string.IsNullOrWhiteSpace(snapshot.Identity.OwnerId)
            ? ownerUserId
            : snapshot.Identity.OwnerId!;
        string snapshotColonyId = string.IsNullOrWhiteSpace(snapshot.Identity.ColonyId)
            ? colonyId
            : snapshot.Identity.ColonyId!;
        string? snapshotId = snapshot.Identity.SnapshotId;
        PlayerSessionRecord? player = state.Players.FindByUserId(snapshotOwnerId);
        if (player is not null
            && string.Equals(player.ColonyId, snapshotColonyId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(player.LatestSnapshotWealthSnapshotId, snapshotId, StringComparison.Ordinal)
            && player.LatestSnapshotWealth.HasValue)
        {
            return Math.Max(0, player.LatestSnapshotWealth.Value);
        }

        int? wealth = CalculateSnapshotWealth(snapshot)
            ?? CalculateLatestSnapshotWealthFromPackage(state, snapshotOwnerId, snapshotColonyId, snapshot.Identity);
        if (wealth.HasValue)
        {
            state.Players.RecordLatestSnapshotWealth(
                snapshotOwnerId,
                snapshotColonyId,
                snapshotId,
                wealth,
                nowUtc);
            return Math.Max(0, wealth.Value);
        }

        return int.MaxValue;
    }

    private static IReadOnlyList<WorldMapMarker> BuildConfiguredPlayerColonyMarkers(
        WorldConfigurationDto? configuration,
        DateTimeOffset nowUtc)
    {
        if (configuration?.PlayerColonySites.Count is not > 0)
        {
            return Array.Empty<WorldMapMarker>();
        }

        return configuration.PlayerColonySites
            .Where(site => !string.IsNullOrWhiteSpace(site.UserId))
            .Select(site =>
            {
                string worldObjectId = string.IsNullOrWhiteSpace(site.WorldObjectId)
                    ? $"tile:{site.Tile},{site.TileLayerId}"
                    : site.WorldObjectId!;
                return new WorldMapMarker(
                    $"tradeable-colony:{site.UserId}:{worldObjectId}",
                    WorldMapMarkerKind.TradeableColony,
                    site.UserId,
                    site.ColonyId,
                    worldObjectId,
                    site.MapUniqueId,
                    SnapshotId: null,
                    site.Tile,
                    string.IsNullOrWhiteSpace(site.Label) ? site.ColonyId : site.Label,
                    nowUtc,
                    RelatedEventId: null,
                    TradeEnabled: true,
                    ReinforcementEnabled: false,
                    RaidAvailability: null,
                    IconDefName: "Settlement",
                    OwnerFactionName: site.FactionName,
                    Appearance: site.Appearance,
                    TileLayerId: site.TileLayerId);
            })
            .ToList();
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
            && ledgerEvent.Payload is ServerNotificationEventPayload payload
            && string.Equals(payload.NotificationId, BuildColonyTombstoneNotificationId(userId, colonyId), StringComparison.Ordinal));
    }

    private static void AppendColonyTombstoneEvent(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? abandonedSnapshotId,
        DateTimeOffset nowUtc)
    {
        string notificationId = BuildColonyTombstoneNotificationId(userId, colonyId);
        string idempotencyKey = "colony-abandoned:" + userId + ":" + colonyId;
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
        state.Ledger.Append(tombstone);
    }

    private static string BuildColonyTombstoneNotificationId(string userId, string colonyId)
    {
        return "colony-tombstone:" + userId + ":" + colonyId;
    }

    private static RaidEligibilityPolicy BuildRaidEligibilityPolicy(ClashOfRimServerConfiguration configuration)
    {
        return new RaidEligibilityPolicy(
            configuration.RaidMinimumDefenderWealth,
            RequireHostileRelation: true,
            RequireDefenderOffline: true);
    }

    private static RaidCooldownPolicy BuildRaidCooldownPolicy(ClashOfRimServerConfiguration configuration)
    {
        return new RaidCooldownPolicy(
            configuration.RaidProtectionDuration,
            RaidCooldownPolicy.Default.TimeoutCooldown,
            RaidCooldownPolicy.Default.CancelledCooldown);
    }

    private static RaidDefenseLockPolicy BuildRaidDefenseLockPolicy(ClashOfRimServerConfiguration configuration)
    {
        return new RaidDefenseLockPolicy(
            configuration.RaidMaxDuration,
            configuration.RaidTimeoutGracePeriod);
    }
}
