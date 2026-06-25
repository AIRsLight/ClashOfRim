using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public sealed class RuntimeWorldObjectMarkerRegistry
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(90);
    private readonly object sync = new();
    private readonly Dictionary<string, RuntimeWorldObjectMarkerRecord> recordsByMarkerId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> markerIdsByOwner = new(StringComparer.Ordinal);
    private readonly TimeSpan lease;

    public RuntimeWorldObjectMarkerRegistry(TimeSpan? lease = null)
    {
        this.lease = lease ?? DefaultLease;
    }

    public int ReplaceForOwner(
        string userId,
        string colonyId,
        string? snapshotId,
        IEnumerable<RuntimeWorldObjectMarkerDto> markers,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        ArgumentNullException.ThrowIfNull(markers);

        lock (sync)
        {
            RemoveExpiredLocked(nowUtc);
            string ownerKey = OwnerKey(userId, colonyId);
            RemoveByOwnerKeyLocked(ownerKey);

            int accepted = 0;
            foreach (RuntimeWorldObjectMarkerDto marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker.WorldObjectId) || marker.Tile < 0)
                {
                    continue;
                }

                string markerId = MarkerPrefix(userId, colonyId) + NormalizeKey(marker.WorldObjectId);
                recordsByMarkerId[markerId] = new RuntimeWorldObjectMarkerRecord(
                    markerId,
                    ownerKey,
                    userId,
                    colonyId,
                    snapshotId,
                    marker.WorldObjectId,
                    marker.DefName,
                    ResolveKind(marker.Kind),
                    marker.Tile,
                    Math.Max(0, marker.TileLayerId),
                    marker.Label,
                    marker.PathTiles,
                    nowUtc,
                    nowUtc + lease);
                AddOwnerMarkerLocked(ownerKey, markerId);
                accepted++;
            }

            return accepted;
        }
    }

    public int RemoveForOwner(string userId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (sync)
        {
            string ownerKey = OwnerKey(userId, colonyId);
            int removed = markerIdsByOwner.TryGetValue(ownerKey, out HashSet<string>? markerIds)
                ? markerIds.Count
                : 0;
            RemoveByOwnerKeyLocked(ownerKey);

            return removed;
        }
    }

    public IReadOnlyList<WorldMapMarker> ListVisibleMarkers(string viewerUserId, DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            RemoveExpiredLocked(nowUtc);
            var markers = new List<WorldMapMarker>(recordsByMarkerId.Count);
            foreach (RuntimeWorldObjectMarkerRecord record in recordsByMarkerId.Values)
            {
                if (string.Equals(record.OwnerUserId, viewerUserId, StringComparison.Ordinal))
                {
                    continue;
                }

                markers.Add(new WorldMapMarker(
                    record.MarkerId,
                    record.Kind,
                    record.OwnerUserId,
                    record.ColonyId,
                    record.WorldObjectId,
                    MapUniqueId: null,
                    record.SnapshotId,
                    record.Tile,
                    record.Label,
                    record.UpdatedAtUtc,
                    RelatedEventId: null,
                    TradeEnabled: false,
                    ReinforcementEnabled: false,
                    RaidAvailability: null,
                    record.DefName,
                    PathTiles: record.PathTiles,
                    TileLayerId: record.TileLayerId));
            }

            return markers;
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset nowUtc)
    {
        List<string>? expiredIds = null;
        foreach (KeyValuePair<string, RuntimeWorldObjectMarkerRecord> pair in recordsByMarkerId)
        {
            if (pair.Value.ExpiresAtUtc > nowUtc)
            {
                continue;
            }

            expiredIds ??= new List<string>();
            expiredIds.Add(pair.Key);
        }

        if (expiredIds is not null)
        {
            RemoveMarkersLocked(expiredIds);
        }
    }

    private void RemoveByOwnerKeyLocked(string ownerKey)
    {
        if (!markerIdsByOwner.TryGetValue(ownerKey, out HashSet<string>? markerIds))
        {
            return;
        }

        var ids = new List<string>(markerIds.Count);
        foreach (string markerId in markerIds)
        {
            ids.Add(markerId);
        }

        RemoveMarkersLocked(ids);
    }

    private void AddOwnerMarkerLocked(string ownerKey, string markerId)
    {
        if (!markerIdsByOwner.TryGetValue(ownerKey, out HashSet<string>? markerIds))
        {
            markerIds = new HashSet<string>(StringComparer.Ordinal);
            markerIdsByOwner[ownerKey] = markerIds;
        }

        markerIds.Add(markerId);
    }

    private void RemoveMarkersLocked(List<string> markerIds)
    {
        for (int i = 0; i < markerIds.Count; i++)
        {
            string markerId = markerIds[i];
            if (!recordsByMarkerId.Remove(markerId, out RuntimeWorldObjectMarkerRecord? removed))
            {
                continue;
            }

            if (!markerIdsByOwner.TryGetValue(removed.OwnerKey, out HashSet<string>? ownerMarkerIds))
            {
                continue;
            }

            ownerMarkerIds.Remove(markerId);
            if (ownerMarkerIds.Count == 0)
            {
                markerIdsByOwner.Remove(removed.OwnerKey);
            }
        }
    }

    private static WorldMapMarkerKind ResolveKind(string? kind)
    {
        if (string.Equals(kind, "Caravan", StringComparison.OrdinalIgnoreCase))
        {
            return WorldMapMarkerKind.RuntimeCaravan;
        }

        if (string.Equals(kind, "Shuttle", StringComparison.OrdinalIgnoreCase))
        {
            return WorldMapMarkerKind.RuntimeShuttle;
        }

        if (string.Equals(kind, "TransportPod", StringComparison.OrdinalIgnoreCase))
        {
            return WorldMapMarkerKind.RuntimeTransportPod;
        }

        return WorldMapMarkerKind.RuntimeWorldObject;
    }

    private static string MarkerPrefix(string userId, string colonyId)
    {
        return $"runtime-world-object:{NormalizeKey(userId)}:{NormalizeKey(colonyId)}:";
    }

    private static string OwnerKey(string userId, string colonyId)
    {
        return userId + "\n" + colonyId;
    }

    private static string NormalizeKey(string value)
    {
        char[] chars = new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            chars[i] = char.IsLetterOrDigit(ch) ? ch : '-';
        }

        return new string(chars);
    }

    private sealed record RuntimeWorldObjectMarkerRecord(
        string MarkerId,
        string OwnerKey,
        string OwnerUserId,
        string ColonyId,
        string? SnapshotId,
        string WorldObjectId,
        string? DefName,
        WorldMapMarkerKind Kind,
        int Tile,
        int TileLayerId,
        string? Label,
        IReadOnlyList<int> PathTiles,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
