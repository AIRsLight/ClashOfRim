using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public static class WorldMapMarkerProjectionBuilder
{
    public static IReadOnlyList<WorldMapMarker> Build(
        SnapshotIdentity snapshotIdentity,
        IEnumerable<WorldObjectSummary> worldObjects,
        IEnumerable<MapSummary> maps,
        IEnumerable<AuthoritativeEvent> events,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshotIdentity);
        ArgumentNullException.ThrowIfNull(worldObjects);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(events);

        var markers = new List<WorldMapMarker>(BuildActiveRaidTargetMarkers(events, nowUtc));
        markers.Sort(CompareMarkers);
        return markers;
    }

    public static IReadOnlyList<WorldMapMarker> BuildTradeableColonyMarkers(
        SnapshotIdentity snapshotIdentity,
        IEnumerable<WorldObjectSummary> worldObjects,
        IEnumerable<MapSummary> maps,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshotIdentity);
        ArgumentNullException.ThrowIfNull(worldObjects);
        ArgumentNullException.ThrowIfNull(maps);

        if (string.IsNullOrWhiteSpace(snapshotIdentity.OwnerId))
        {
            return Array.Empty<WorldMapMarker>();
        }

        var mapUniqueIdByParent = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (MapSummary map in maps)
        {
            if (string.IsNullOrWhiteSpace(map.ParentWorldObjectId)
                || mapUniqueIdByParent.ContainsKey(map.ParentWorldObjectId!))
            {
                continue;
            }

            mapUniqueIdByParent[map.ParentWorldObjectId!] = map.UniqueId;
        }

        var markers = new List<WorldMapMarker>();
        foreach (WorldObjectSummary worldObject in worldObjects)
        {
            if (worldObject.Destroyed || !IsTradeableColony(worldObject))
            {
                continue;
            }

            WorldMapMarker? marker = ToTradeableColonyMarker(snapshotIdentity, worldObject, mapUniqueIdByParent, nowUtc);
            if (marker is not null)
            {
                markers.Add(marker);
            }
        }

        return markers;
    }

    public static IReadOnlyList<WorldMapMarker> BuildActiveRaidTargetMarkers(
        IEnumerable<AuthoritativeEvent> events,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(events);

        var markers = new List<WorldMapMarker>();
        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (ledgerEvent.Type != ServerEventType.Raid
                || !IsActiveRaidEvent(ledgerEvent)
                || ledgerEvent.TargetContext?.Tile is null)
            {
                continue;
            }

            RaidEventPayload? payload = ledgerEvent.Payload as RaidEventPayload;
            markers.Add(new WorldMapMarker(
                $"active-raid:{ledgerEvent.EventId}",
                WorldMapMarkerKind.ActiveRaidTarget,
                ledgerEvent.Target.UserId,
                ledgerEvent.Target.ColonyId,
                ledgerEvent.TargetContext.WorldObjectId,
                ledgerEvent.TargetContext.MapUniqueId,
                payload?.DefenderSnapshotId,
                ledgerEvent.TargetContext.Tile.Value,
                ServerLocalization.Text("WorldMap.ActivePlayerRaidLabel"),
                nowUtc,
                ledgerEvent.EventId,
                TradeEnabled: false,
                ReinforcementEnabled: true));
        }

        return markers;
    }

    private static int CompareMarkers(WorldMapMarker left, WorldMapMarker right)
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

    private static WorldMapMarker? ToTradeableColonyMarker(
        SnapshotIdentity snapshotIdentity,
        WorldObjectSummary worldObject,
        IReadOnlyDictionary<string, string?> mapUniqueIdByParent,
        DateTimeOffset nowUtc)
    {
        if (!TryParsePlanetTile(worldObject.Tile, out int tile, out int tileLayerId))
        {
            return null;
        }

        string worldObjectId = worldObject.UniqueLoadId ?? worldObject.Id ?? $"tile:{tile},{tileLayerId}";
        mapUniqueIdByParent.TryGetValue(worldObjectId, out string? mapUniqueId);

        return new WorldMapMarker(
            $"tradeable-colony:{snapshotIdentity.OwnerId}:{worldObjectId}",
            WorldMapMarkerKind.TradeableColony,
            snapshotIdentity.OwnerId!,
            snapshotIdentity.ColonyId,
            worldObjectId,
            mapUniqueId,
            snapshotIdentity.SnapshotId,
            tile,
            worldObject.Name,
            nowUtc,
            RelatedEventId: null,
            TradeEnabled: true,
            ReinforcementEnabled: false,
            RaidAvailability: null,
            IconDefName: worldObject.Def,
            TileLayerId: tileLayerId);
    }

    private static bool IsTradeableColony(WorldObjectSummary worldObject)
    {
        return string.Equals(worldObject.Def, "PlayerColony", StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(worldObject.Class)
                && worldObject.Class.Contains("PlayerColony", StringComparison.Ordinal));
    }

    private static bool TryParsePlanetTile(string? tileText, out int tile, out int layerId)
    {
        tile = -1;
        layerId = 0;
        if (string.IsNullOrWhiteSpace(tileText))
        {
            return false;
        }

        string text = tileText!.Trim();
        string[] parts = text.Split(',', 2);
        if (!int.TryParse(parts[0].Trim(), out tile) || tile < 0)
        {
            return false;
        }

        if (parts.Length == 2
            && (!int.TryParse(parts[1].Trim(), out layerId) || layerId < 0))
        {
            return false;
        }

        return true;
    }

    private static bool IsActiveRaidEvent(AuthoritativeEvent ledgerEvent)
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
}
