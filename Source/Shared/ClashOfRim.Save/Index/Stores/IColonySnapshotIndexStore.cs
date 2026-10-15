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

    // Returns one coherent version captured during the call. A concurrent publication or removal
    // may finish before decoding does; callers that mutate state must revalidate their baseline.
    SaveSnapshotPackage? GetLatestPackage(string ownerId, string colonyId);
}

public interface IColonySnapshotHistoryStore : IColonySnapshotIndexStore
{
    bool HasAcceptedOriginalHash(string ownerId, string colonyId, string originalSha256);
}

/// <summary>Stores that can prepare payload compression before the caller enters its business mutation gate.</summary>
public interface IPreparedColonySnapshotPackageStore : IColonySnapshotPackageStore
{
    PreparedSnapshotPayload PreparePayload(byte[] payload);

    void StorePrepared(SaveSnapshotPackage package, SaveSnapshotIndex rebuiltIndex, DateTimeOffset acceptedAtUtc,
        PreparedSnapshotPayload preparedPayload);
}
