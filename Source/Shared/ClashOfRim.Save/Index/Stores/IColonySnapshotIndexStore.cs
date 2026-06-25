namespace AIRsLight.ClashOfRim.Save;

public interface IColonySnapshotIndexStore
{
    void StoreLatest(LatestSnapshotRecord snapshot);

    LatestSnapshotRecord? GetLatest(string ownerId, string colonyId);

    IReadOnlyList<LatestSnapshotRecord> ListLatest();

    bool RemoveLatest(string ownerId, string colonyId);
}

public interface IColonySnapshotPackageStore : IColonySnapshotIndexStore
{
    void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex rebuiltIndex, DateTimeOffset acceptedAtUtc);

    SaveSnapshotPackage? GetLatestPackage(string ownerId, string colonyId);
}

public interface IColonySnapshotHistoryStore : IColonySnapshotIndexStore
{
    bool HasAcceptedOriginalHash(string ownerId, string colonyId, string originalSha256);
}
