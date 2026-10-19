using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal sealed class SnapshotRecoveryTestStore(string root) : IPreparedColonySnapshotPackageStore
{
    public FileColonySnapshotIndexStore Inner { get; } = new(root);
    public Action? BeforePrepare;
    public object? Gate;
    public bool FailPublication, FailPreparation;
    public int PreparedWrites, RawWrites;
    public int Writes => PreparedWrites + RawWrites;
    public void Reset() { PreparedWrites = 0; RawWrites = 0; }
    public PreparedSnapshotPayload PreparePayload(byte[] bytes)
    {
        BeforePrepare?.Invoke();
        if (FailPreparation) throw new IOException("preparation failed");
        return Inner.PreparePayload(bytes);
    }
    public void StorePrepared(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at, PreparedSnapshotPayload prepared)
    {
        Check(Gate is null || Monitor.IsEntered(Gate), "Snapshot recovery scope did not retain the mutation gate for publication.");
        if (FailPublication) throw new IOException("publication failed");
        Inner.StorePrepared(p, i, at, prepared); PreparedWrites++;
    }
    public void StoreLatest(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at)
    {
        Check(Gate is null || Monitor.IsEntered(Gate), "Snapshot recovery scope did not retain the mutation gate for publication.");
        if (FailPublication) throw new IOException("publication failed");
        Inner.StoreLatest(p, i, at); RawWrites++;
    }
    public void StoreLatest(LatestSnapshotRecord record) => Inner.StoreLatest(record);
    public LatestSnapshotRecord? GetLatest(string owner, string colony) => Inner.GetLatest(owner, colony);
    public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => Inner.GetLatestPackage(owner, colony);
    public IReadOnlyList<LatestSnapshotRecord> ListLatest() => Inner.ListLatest();
    public bool RemoveLatest(string owner, string colony) => Inner.RemoveLatest(owner, colony);
}
