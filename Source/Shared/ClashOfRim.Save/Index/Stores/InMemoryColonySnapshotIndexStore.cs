namespace AIRsLight.ClashOfRim.Save;

public sealed class InMemoryColonySnapshotIndexStore : IColonySnapshotHistoryStore
{
    private readonly Dictionary<string, LatestSnapshotRecord> latestByColony = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> acceptedOriginalHashesByColony = new(StringComparer.Ordinal);

    public void StoreLatest(LatestSnapshotRecord snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Identity.OwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Identity.ColonyId);

        string key = Key(snapshot.Identity.OwnerId, snapshot.Identity.ColonyId);
        latestByColony[key] = snapshot;
        if (!string.IsNullOrWhiteSpace(snapshot.Envelope.OriginalSha256))
        {
            GetAcceptedOriginalHashes(key).Add(snapshot.Envelope.OriginalSha256);
        }
    }

    public LatestSnapshotRecord? GetLatest(string ownerId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        latestByColony.TryGetValue(Key(ownerId, colonyId), out LatestSnapshotRecord? snapshot);
        return snapshot;
    }

    public IReadOnlyList<LatestSnapshotRecord> ListLatest()
    {
        return latestByColony.Values
            .OrderBy(snapshot => snapshot.Identity.OwnerId, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.Identity.ColonyId, StringComparer.Ordinal)
            .ToList();
    }

    public bool RemoveLatest(string ownerId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        string key = Key(ownerId, colonyId);
        acceptedOriginalHashesByColony.Remove(key);
        return latestByColony.Remove(key);
    }

    public bool HasAcceptedOriginalHash(string ownerId, string colonyId, string originalSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        return !string.IsNullOrWhiteSpace(originalSha256)
            && acceptedOriginalHashesByColony.TryGetValue(Key(ownerId, colonyId), out HashSet<string>? hashes)
            && hashes.Contains(originalSha256);
    }

    private HashSet<string> GetAcceptedOriginalHashes(string key)
    {
        if (!acceptedOriginalHashesByColony.TryGetValue(key, out HashSet<string>? hashes))
        {
            hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            acceptedOriginalHashesByColony[key] = hashes;
        }

        return hashes;
    }

    private static string Key(string ownerId, string colonyId)
    {
        return $"{ownerId}\u001f{colonyId}";
    }
}
