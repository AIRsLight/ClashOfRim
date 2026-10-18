using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public sealed class RuntimeWorldObjectMarkerRegistry
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(90);
    private readonly object sync = new();
    private readonly Dictionary<(string UserId, string ColonyId), PublishedBatch> byOwner = new();
    private readonly TimeSpan lease;

    public RuntimeWorldObjectMarkerRegistry(TimeSpan? lease = null) => this.lease = lease ?? DefaultLease;

    public int ReplaceForOwner(string userId, string colonyId, string? snapshotId,
        IEnumerable<RuntimeWorldObjectMarkerDto> markers, DateTimeOffset nowUtc)
        => Publish(Prepare(userId, colonyId, snapshotId, markers), nowUtc);

    // Capture the complete immutable batch before replacing any live state or taking a mutation gate.
    internal PreparedRuntimeWorldObjects Prepare(string userId, string colonyId, string? snapshotId,
        IEnumerable<RuntimeWorldObjectMarkerDto> markers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        ArgumentNullException.ThrowIfNull(markers);
        string prefix = $"runtime-world-object:{Uri.EscapeDataString(userId)}:{Uri.EscapeDataString(colonyId)}:";
        var prepared = new Dictionary<string, WorldMapMarker>(StringComparer.Ordinal);
        int accepted = 0;
        foreach (var marker in markers)
        {
            if (marker is null || string.IsNullOrWhiteSpace(marker.WorldObjectId) || marker.Tile < 0) continue;
            prepared[marker.WorldObjectId] = new WorldMapMarker(
                prefix + Uri.EscapeDataString(marker.WorldObjectId), ResolveKind(marker.Kind), userId, colonyId,
                marker.WorldObjectId, MapUniqueId: null, snapshotId, marker.Tile, marker.Label, CreatedAtUtc: default,
                RelatedEventId: null, TradeEnabled: false, ReinforcementEnabled: false, RaidAvailability: null,
                marker.DefName, PathTiles: Array.AsReadOnly(marker.PathTiles.ToArray()), TileLayerId: Math.Max(0, marker.TileLayerId));
            // Preserve the wire count of valid inputs; the last duplicate world-object ID wins.
            accepted++;
        }
        return new(userId, colonyId, Array.AsReadOnly(prepared.Values.ToArray()), accepted);
    }

    internal int Publish(PreparedRuntimeWorldObjects batch, DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            RemoveExpiredLocked(nowUtc);
            var owner = (batch.UserId, batch.ColonyId);
            if (batch.Markers.Count == 0) byOwner.Remove(owner);
            else byOwner[owner] = new(batch.Markers, nowUtc, nowUtc + lease);
            return batch.AcceptedCount;
        }
    }

    public int RemoveForOwner(string userId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);
        lock (sync) return byOwner.Remove((userId, colonyId), out var removed) ? removed.Markers.Count : 0;
    }

    public IReadOnlyList<WorldMapMarker> ListVisibleMarkers(string viewerUserId, DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            RemoveExpiredLocked(nowUtc);
            return byOwner.Where(pair => pair.Key.UserId != viewerUserId)
                .SelectMany(pair => pair.Value.Markers.Select(marker => marker with { CreatedAtUtc = pair.Value.UpdatedAtUtc })).ToArray();
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset nowUtc)
    {
        foreach (var owner in byOwner.Where(pair => pair.Value.ExpiresAtUtc <= nowUtc).Select(pair => pair.Key).ToArray()) byOwner.Remove(owner);
    }

    private sealed record PublishedBatch(IReadOnlyList<WorldMapMarker> Markers, DateTimeOffset UpdatedAtUtc, DateTimeOffset ExpiresAtUtc);

    private static WorldMapMarkerKind ResolveKind(string? kind)
    {
        if (string.Equals(kind, "Caravan", StringComparison.OrdinalIgnoreCase)) return WorldMapMarkerKind.RuntimeCaravan;
        if (string.Equals(kind, "Shuttle", StringComparison.OrdinalIgnoreCase)) return WorldMapMarkerKind.RuntimeShuttle;
        if (string.Equals(kind, "TransportPod", StringComparison.OrdinalIgnoreCase)) return WorldMapMarkerKind.RuntimeTransportPod;
        return WorldMapMarkerKind.RuntimeWorldObject;
    }
}

internal sealed record PreparedRuntimeWorldObjects(string UserId, string ColonyId, IReadOnlyList<WorldMapMarker> Markers,
    int AcceptedCount);
