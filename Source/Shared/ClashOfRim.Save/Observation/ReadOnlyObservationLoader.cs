namespace AIRsLight.ClashOfRim.Save;

public sealed class ReadOnlyObservationLoader
{
    private readonly IColonySnapshotIndexStore store;

    public ReadOnlyObservationLoader(IColonySnapshotIndexStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ObservationLoadResult Load(ReadOnlyObservationRequest request, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (string.IsNullOrWhiteSpace(request.ObserverUserId))
        {
            return ObservationLoadResult.Reject(
                ObservationLoadResultKind.MissingObserver,
                "Read-only observation request is missing the observer player.");
        }

        SnapshotIdentity identity = request.TargetSnapshot;
        if (string.IsNullOrWhiteSpace(identity.OwnerId)
            || string.IsNullOrWhiteSpace(identity.ColonyId)
            || string.IsNullOrWhiteSpace(identity.SnapshotId))
        {
            return ObservationLoadResult.Reject(
                ObservationLoadResultKind.MissingTargetSnapshotIdentity,
                "Read-only observation request is missing the target snapshot identity.");
        }

        LatestSnapshotRecord? snapshot = store.GetLatest(identity.OwnerId, identity.ColonyId);
        if (snapshot is null || !string.Equals(snapshot.Identity.SnapshotId, identity.SnapshotId, StringComparison.Ordinal))
        {
            return ObservationLoadResult.Reject(
                ObservationLoadResultKind.SnapshotNotFound,
                "Target snapshot does not exist or is not the latest snapshot for that colony.");
        }

        MapSummary? map = snapshot.Index.Maps.FirstOrDefault(candidate =>
            string.Equals(candidate.UniqueId, request.TargetMap.MapUniqueId, StringComparison.Ordinal));
        if (map is null)
        {
            return ObservationLoadResult.Reject(
                ObservationLoadResultKind.MapNotFound,
                "Requested map does not exist in the target snapshot.");
        }

        if (!MapContextMatches(request.TargetMap, map, snapshot.Index.WorldObjects))
        {
            return ObservationLoadResult.Reject(
                ObservationLoadResultKind.MapContextMismatch,
                "Requested map does not match the target world object or tile.");
        }

        var session = new ObservationSession(
            sessionId,
            request,
            snapshot,
            map,
            ObservationModeRestrictions.ServerIsolatedSandbox);

        return ObservationLoadResult.Grant(session);
    }

    private static bool MapContextMatches(
        ObservationMapContext request,
        MapSummary map,
        IReadOnlyList<WorldObjectSummary> worldObjects)
    {
        if (!string.IsNullOrWhiteSpace(request.WorldObjectId)
            && !string.Equals(map.ParentWorldObjectId, request.WorldObjectId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Tile))
        {
            WorldObjectSummary? worldObject = worldObjects.FirstOrDefault(candidate =>
                string.Equals(candidate.UniqueLoadId, request.WorldObjectId, StringComparison.Ordinal)
                || string.Equals(candidate.Id, request.WorldObjectId, StringComparison.Ordinal));

            if (worldObject is not null
                && !string.Equals(worldObject.Tile, request.Tile, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
