namespace AIRsLight.ClashOfRim.Events;

public static class WorldMapRaidAvailabilityAttacher
{
    public static IReadOnlyList<WorldMapMarker> Attach(
        string attackerUserId,
        IEnumerable<WorldMapMarker> markers,
        IEnumerable<WorldMapRaidAvailabilitySource> sources,
        DateTimeOffset nowUtc,
        RaidEligibilityPolicy? defaultPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attackerUserId);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(sources);

        var sourceByColonyKey = new Dictionary<string, WorldMapRaidAvailabilitySource>(StringComparer.Ordinal);
        foreach (WorldMapRaidAvailabilitySource source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.DefenderSnapshot.OwnerId))
            {
                continue;
            }

            string key = ColonyKey(source.DefenderSnapshot.OwnerId!, source.DefenderSnapshot.ColonyId);
            if (!sourceByColonyKey.ContainsKey(key))
            {
                sourceByColonyKey[key] = source;
            }
        }

        var attachedMarkers = new List<WorldMapMarker>();
        foreach (WorldMapMarker marker in markers)
        {
            attachedMarkers.Add(AttachToMarker(attackerUserId, marker, sourceByColonyKey, nowUtc, defaultPolicy));
        }

        return attachedMarkers;
    }

    private static WorldMapMarker AttachToMarker(
        string attackerUserId,
        WorldMapMarker marker,
        IReadOnlyDictionary<string, WorldMapRaidAvailabilitySource> sourceByColonyKey,
        DateTimeOffset nowUtc,
        RaidEligibilityPolicy? defaultPolicy)
    {
        if (marker.Kind != WorldMapMarkerKind.TradeableColony ||
            string.IsNullOrWhiteSpace(marker.OwnerUserId) ||
            !sourceByColonyKey.TryGetValue(ColonyKey(marker.OwnerUserId, marker.ColonyId), out WorldMapRaidAvailabilitySource? source))
        {
            return marker;
        }

        var eligibility = new RaidEligibilityRequest(
            new EventParty(attackerUserId),
            new EventParty(source.DefenderSnapshot.OwnerId!, source.DefenderSnapshot.ColonyId),
            source.IsHostile,
            source.DefenderOnline,
            nowUtc,
            source.DefenderRaidCooldownUntilUtc,
            source.DefenderWealth,
            source.DefenderSnapshot,
            source.DefenderMaps,
            marker.MapUniqueId);

        RaidAvailabilitySummary availability = RaidAvailabilityProjector.Project(
            new RaidAvailabilityProjectionRequest(eligibility, source.Policy ?? defaultPolicy));

        return marker with { RaidAvailability = availability };
    }

    private static string ColonyKey(string ownerUserId, string? colonyId)
    {
        return ownerUserId + "|" + (colonyId ?? "");
    }
}
