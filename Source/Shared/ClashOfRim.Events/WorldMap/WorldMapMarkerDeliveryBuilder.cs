namespace AIRsLight.ClashOfRim.Events;

public static class WorldMapMarkerDeliveryBuilder
{
    public static WorldMapMarkerDelivery BuildForLogin(
        string userId,
        IEnumerable<WorldMapMarkerSource> colonySources,
        IEnumerable<AuthoritativeEvent> events,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(colonySources);
        ArgumentNullException.ThrowIfNull(events);

        var markersById = new Dictionary<string, WorldMapMarker>(StringComparer.Ordinal);
        foreach (WorldMapMarker marker in WorldMapMarkerProjectionBuilder.BuildActiveRaidTargetMarkers(events, nowUtc))
        {
            if (!markersById.ContainsKey(marker.MarkerId))
            {
                markersById[marker.MarkerId] = marker;
            }
        }

        var orderedMarkers = new List<WorldMapMarker>(markersById.Count);
        foreach (WorldMapMarker marker in markersById.Values)
        {
            orderedMarkers.Add(marker);
        }

        orderedMarkers.Sort(CompareMarkers);

        return new WorldMapMarkerDelivery(userId, nowUtc, orderedMarkers);
    }

    public static WorldMapMarkerDelivery BuildForLogin(
        string userId,
        IEnumerable<WorldMapMarkerSource> colonySources,
        IEnumerable<AuthoritativeEvent> events,
        IEnumerable<WorldMapRaidAvailabilitySource> raidAvailabilitySources,
        DateTimeOffset nowUtc,
        RaidEligibilityPolicy? defaultRaidPolicy = null)
    {
        WorldMapMarkerDelivery delivery = BuildForLogin(userId, colonySources, events, nowUtc);
        IReadOnlyList<WorldMapMarker> markers = WorldMapRaidAvailabilityAttacher.Attach(
            userId,
            delivery.Markers,
            raidAvailabilitySources,
            nowUtc,
            defaultRaidPolicy);

        return delivery with { Markers = markers };
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
}
