using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal sealed class WorldMarkerDelivery(PlayerRegistry playerRegistry, OnlinePresenceRegistry presence,
    DiplomacyRelationRegistry relations, IColonySnapshotIndexStore snapshots, WorldConfigurationRegistry world,
    WorldConfigurationDelivery worldDelivery, RuntimeWorldObjectMarkerRegistry runtime, RaidMarkerProjection raids,
    RaidProtection protection, PlayerSnapshotWealth wealth, Func<ClashOfRimServerConfiguration> configuration)
{
    internal WorldMapMarkerDelivery Build(
        string userId,
        string? colonyId,
        DateTimeOffset nowUtc)
    {
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId = BuildPlayerLookupByUserId();
        bool lockedToActiveRaid = raids.HasActor(userId, colonyId);
        WorldMapMarkerDelivery delivery = new(
            userId,
            nowUtc,
            raids.Build(
                nowUtc,
                lockedToActiveRaid ? userId : null,
                lockedToActiveRaid ? colonyId : null,
                userId,
                colonyId),
            configuration().GiftsEnabled,
            configuration().PvpEnabled);
        if (lockedToActiveRaid)
        {
            IReadOnlyList<WorldMapMarker> raidOnlyMarkers = ContextualizeAndSortMarkers(
                userId,
                colonyId,
                delivery.Markers,
                playersByUserId);
            return delivery with { Markers = raidOnlyMarkers };
        }

        WorldConfigurationDto? deliveredConfiguration = worldDelivery.Build(world.Current);
        IReadOnlyList<WorldMapMarker> baselineMarkers = BuildConfiguredPlayerColonyMarkers(
            deliveredConfiguration,
            nowUtc);
        baselineMarkers = WorldMapRaidAvailabilityAttacher.Attach(
            userId,
            baselineMarkers,
            BuildRaidAvailabilitySources(userId, colonyId, baselineMarkers, nowUtc),
            nowUtc,
            RaidPolicies.BuildEligibility(configuration()));
        baselineMarkers = WithFriendlyReinforcementAvailability(userId, colonyId, baselineMarkers);
        IReadOnlyList<WorldMapMarker> runtimeMarkers = runtime.ListVisibleMarkers(userId, nowUtc);
        IReadOnlyList<WorldMapMarker> markers = MergeContextualizeAndSortMarkers(
            userId,
            colonyId,
            delivery.Markers,
            baselineMarkers,
            runtimeMarkers,
            playersByUserId);
        return delivery with { Markers = markers };
    }

    private IReadOnlyList<WorldMapMarker> ContextualizeAndSortMarkers(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> markers,
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId)
    {
        var result = new List<WorldMapMarker>(markers.Count);
        foreach (WorldMapMarker marker in markers)
        {
            result.Add(WithViewerMarkerContext(viewerUserId, viewerColonyId, marker, playersByUserId));
        }

        SortWorldMapMarkers(result);
        return result;
    }

    private IReadOnlyList<WorldMapMarker> MergeContextualizeAndSortMarkers(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> raidMarkers,
        IReadOnlyList<WorldMapMarker> baselineMarkers,
        IReadOnlyList<WorldMapMarker> runtimeMarkers,
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId)
    {
        int capacity = raidMarkers.Count + baselineMarkers.Count + runtimeMarkers.Count;
        var seenMarkerIds = new HashSet<string>(capacity, StringComparer.Ordinal);
        var result = new List<WorldMapMarker>(capacity);
        AddWorldMapMarkers(viewerUserId, viewerColonyId, raidMarkers, playersByUserId, seenMarkerIds, result);
        AddWorldMapMarkers(viewerUserId, viewerColonyId, baselineMarkers, playersByUserId, seenMarkerIds, result);
        AddWorldMapMarkers(viewerUserId, viewerColonyId, runtimeMarkers, playersByUserId, seenMarkerIds, result);
        SortWorldMapMarkers(result);
        return result;
    }

    private void AddWorldMapMarkers(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> markers,
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

            result.Add(WithViewerMarkerContext(viewerUserId, viewerColonyId, marker, playersByUserId));
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

    private WorldMapMarker WithViewerMarkerContext(
        string viewerUserId,
        string? viewerColonyId,
        WorldMapMarker marker,
        IReadOnlyDictionary<string, PlayerSessionRecord> playersByUserId)
    {
        string ownerUserId = marker.OwnerUserId ?? string.Empty;
        playersByUserId.TryGetValue(ownerUserId, out PlayerSessionRecord? owner);
        string relationKind = relations.GetRelationKind(
            viewerUserId,
            viewerColonyId,
            ownerUserId,
            marker.ColonyId);
        return marker with
        {
            RelationKind = relationKind,
            OwnerOnline = presence.IsUserOnline(ownerUserId),
            OwnerLastSeenAtUtc = owner?.LastSeenAtUtc
        };
    }

    private IReadOnlyDictionary<string, PlayerSessionRecord> BuildPlayerLookupByUserId()
    {
        IReadOnlyList<PlayerSessionRecord> players = playerRegistry.List();
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

    private WorldMapMarker WithFriendlyReinforcementAvailability(
        string viewerUserId,
        string? viewerColonyId,
        WorldMapMarker marker)
    {
        if (marker.Kind != WorldMapMarkerKind.TradeableColony
            || string.IsNullOrWhiteSpace(marker.OwnerUserId)
            || string.Equals(marker.OwnerUserId, viewerUserId, StringComparison.Ordinal)
            || !relations.AreAllied(viewerUserId, viewerColonyId, marker.OwnerUserId, marker.ColonyId)
            || string.IsNullOrWhiteSpace(marker.MapUniqueId))
        {
            return marker;
        }

        LatestSnapshotRecord? snapshot = string.IsNullOrWhiteSpace(marker.ColonyId)
            ? null
            : snapshots.GetLatest(marker.OwnerUserId, marker.ColonyId!);
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

    private IReadOnlyList<WorldMapMarker> WithFriendlyReinforcementAvailability(
        string viewerUserId,
        string? viewerColonyId,
        IReadOnlyList<WorldMapMarker> markers)
    {
        var result = new List<WorldMapMarker>(markers.Count);
        foreach (WorldMapMarker marker in markers)
        {
            result.Add(WithFriendlyReinforcementAvailability(viewerUserId, viewerColonyId, marker));
        }

        return result;
    }

    private IReadOnlyList<WorldMapRaidAvailabilitySource> BuildRaidAvailabilitySources(
        string attackerUserId,
        string? attackerColonyId,
        IEnumerable<WorldMapMarker> markers,
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

            LatestSnapshotRecord? snapshot = snapshots.GetLatest(marker.OwnerUserId!, marker.ColonyId!);
            if (snapshot is null)
            {
                continue;
            }

            string relationKind = relations.GetRelationKind(
                attackerUserId,
                attackerColonyId,
                marker.OwnerUserId,
                marker.ColonyId);
            int defenderWealth = wealth.ForRaid(
                marker.OwnerUserId!,
                marker.ColonyId!,
                snapshot,
                nowUtc);
            sources.Add(new WorldMapRaidAvailabilitySource(
                snapshot.Identity,
                snapshot.Index.Maps,
                string.Equals(relationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal),
                presence.IsUserOnline(marker.OwnerUserId),
                defenderWealth,
                protection.CurrentUntil(marker.OwnerUserId, marker.ColonyId, nowUtc)));
        }

        return sources;
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
}
