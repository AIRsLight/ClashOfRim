using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidMarkerProjection(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots)
{
    internal IReadOnlyList<WorldMapMarker> Build(
        DateTimeOffset nowUtc,
        string? actorUserId = null,
        string? actorColonyId = null,
        string? viewerUserId = null,
        string? viewerColonyId = null)
    {
        IReadOnlyList<AuthoritativeEvent> raidEvents = string.IsNullOrWhiteSpace(actorUserId)
            ? ledger.ListByType(ServerEventType.Raid)
            : ledger.ListByTypeForActor(ServerEventType.Raid, actorUserId!, actorColonyId);

        var markers = new List<WorldMapMarker>();
        foreach (AuthoritativeEvent ledgerEvent in raidEvents)
        {
            if (!IsActiveRaidMarkerEvent(ledgerEvent)
                || (!string.IsNullOrWhiteSpace(actorUserId) && !MatchesParty(ledgerEvent.Actor, actorUserId, actorColonyId))
                || (!string.IsNullOrWhiteSpace(viewerUserId) && MatchesParty(ledgerEvent.Target, viewerUserId, viewerColonyId))
                || ledgerEvent.TargetContext?.Tile is null)
            {
                continue;
            }

            WorldMapMarker? marker = BuildActiveRaidTargetMarker(ledgerEvent, nowUtc);
            if (marker is not null)
            {
                markers.Add(marker);
            }
        }

        return markers;
    }

    internal bool HasActor(
        string userId,
        string? colonyId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        foreach (AuthoritativeEvent ledgerEvent in ledger.ListByTypeForActor(ServerEventType.Raid, userId, colonyId))
        {
            if (IsActiveRaidMarkerEvent(ledgerEvent) && MatchesParty(ledgerEvent.Actor, userId, colonyId))
            {
                return true;
            }
        }

        return false;
    }

    internal bool HasParticipant(
        string userId,
        string? colonyId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        if (HasParticipant(
                ledger.ListByTypeForActor(ServerEventType.Raid, userId, colonyId),
                userId,
                colonyId))
        {
            return true;
        }

        return HasParticipant(
            ledger.ListByTypeForTarget(ServerEventType.Raid, userId, colonyId),
            userId,
            colonyId);
    }

    private static bool HasParticipant(
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

            if (MatchesParty(ledgerEvent.Actor, userId, colonyId)
                || MatchesParty(ledgerEvent.Target, userId, colonyId))
            {
                return true;
            }
        }

        return false;
    }

    private WorldMapMarker? BuildActiveRaidTargetMarker(
        AuthoritativeEvent ledgerEvent,
        DateTimeOffset nowUtc)
    {
        if (ledgerEvent.Payload is not RaidEventPayload payload || ledgerEvent.TargetContext?.Tile is null)
        {
            return null;
        }

        LatestSnapshotRecord? attackerSnapshot = string.IsNullOrWhiteSpace(ledgerEvent.Actor.ColonyId)
            ? null
            : snapshots.GetLatest(
                ledgerEvent.Actor.UserId,
                ledgerEvent.Actor.ColonyId!);
        MapSummary? attackerBattleMap = attackerSnapshot is null
            ? null
            : FindBattleMap(attackerSnapshot.Index, ledgerEvent.EventId);
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
            ServerLocalization.Text("WorldMap.AttackingPlayerLabel"),
            nowUtc,
            ledgerEvent.EventId,
            TradeEnabled: false,
            ReinforcementEnabled: true);
    }

    private static MapSummary? FindBattleMap(SaveSnapshotIndex index, string raidEventId)
    {
        var worlds = index.WorldObjects.Where(worldObject => !worldObject.Destroyed && IsBattleMapObject(worldObject)).ToArray();
        var owned = worlds.Where(worldObject => string.Equals(worldObject.ClashOfRimRelatedEventId, raidEventId, StringComparison.Ordinal)).ToArray();
        // Older battle saves may lack an event tag. Only an unambiguous legacy map
        // can stand in for it; a tagged foreign battle or an observer map cannot.
        var candidates = owned.Length > 0 ? owned : worlds.Where(worldObject => string.IsNullOrWhiteSpace(worldObject.ClashOfRimRelatedEventId));
        var objectIds = candidates
            .SelectMany(worldObject => new[] { worldObject.UniqueLoadId, worldObject.Id })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

        var maps = index.Maps
            .Where(map => !string.IsNullOrWhiteSpace(map.UniqueId))
            .Where(map => !string.IsNullOrWhiteSpace(map.ParentWorldObjectId)
                && objectIds.Contains(map.ParentWorldObjectId!)).Take(2).ToArray();
        return maps.Length == 1 ? maps[0] : null;
    }

    private static bool IsBattleMapObject(WorldObjectSummary worldObject)
    {
        if (worldObject.Def is "ClashOfRim_RemoteScoutMapParent" or "ClashOfRim_RemoteRaidObservationMapParent") return false;
        return worldObject.Def is "ClashOfRim_RemoteSessionMapParent" or "ClashOfRim_RemoteRaidBattleMapParent"
            || (!string.IsNullOrWhiteSpace(worldObject.Class)
                && worldObject.Class.IndexOf("RemoteSessionMapParent", StringComparison.OrdinalIgnoreCase) >= 0);
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

        return ledgerEvent.Status is ServerEventStatus.Recorded
            or ServerEventStatus.ReadyForImmediateDelivery
            or ServerEventStatus.PendingOfflineDelivery
            or ServerEventStatus.DeliveredToClient
            or ServerEventStatus.Conflict;
    }

    internal static bool MatchesParty(EventParty party, string userId, string? colonyId)
        => string.Equals(party.UserId, userId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(colonyId) || string.Equals(party.ColonyId, colonyId, StringComparison.Ordinal));
}
