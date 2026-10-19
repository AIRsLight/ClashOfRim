using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal sealed class SnapshotRecoveryTestStore(string root) : IPreparedColonySnapshotPackageStore
{
    public FileColonySnapshotIndexStore Inner { get; } = new(root);
    public Action? BeforePrepare, AfterRead;
    public SaveSnapshotPackage? ReadOverride;
    public object? Gate;
    public bool FailPublication, FailAfterPublication, FailPreparation;
    public int PreparedWrites, RawWrites;
    public int WriteAttempts, PreparedWriteAttempts, Preparations;
    public Dictionary<string, int> WritesByOwner { get; } = new();
    public int Writes => PreparedWrites + RawWrites;
    public void Reset()
    {
        PreparedWrites = 0; RawWrites = 0;
        WriteAttempts = 0; PreparedWriteAttempts = 0; Preparations = 0;
        WritesByOwner.Clear();
    }
    public PreparedSnapshotPayload PreparePayload(byte[] bytes)
    {
        Interlocked.Increment(ref Preparations);
        BeforePrepare?.Invoke();
        if (FailPreparation) throw new IOException("preparation failed");
        return Inner.PreparePayload(bytes);
    }
    public void StorePrepared(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at, PreparedSnapshotPayload prepared)
        => Publish(p, prepared: true, () => Inner.StorePrepared(p, i, at, prepared));
    public void StoreLatest(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at)
        => Publish(p, prepared: false, () => Inner.StoreLatest(p, i, at));

    private void Publish(SaveSnapshotPackage package, bool prepared, Action write)
    {
        Check(Gate is null || Monitor.IsEntered(Gate), "Snapshot recovery scope did not retain the mutation gate for publication.");
        WriteAttempts++;
        if (prepared) PreparedWriteAttempts++;
        if (FailPublication) throw new IOException("publication failed");
        write();
        if (prepared) PreparedWrites++; else RawWrites++;
        string owner = package.Envelope.Identity.OwnerId!;
        WritesByOwner[owner] = WritesByOwner.GetValueOrDefault(owner) + 1;
        if (FailAfterPublication) throw new IOException("after publication");
    }
    public void StoreLatest(LatestSnapshotRecord record) => Inner.StoreLatest(record);
    public LatestSnapshotRecord? GetLatest(string owner, string colony) => Inner.GetLatest(owner, colony);
    public SaveSnapshotPackage? GetLatestPackage(string owner, string colony)
    {
        var package = ReadOverride ?? Inner.GetLatestPackage(owner, colony);
        AfterRead?.Invoke();
        return package;
    }
    public IReadOnlyList<LatestSnapshotRecord> ListLatest() => Inner.ListLatest();
    public bool RemoveLatest(string owner, string colony) => Inner.RemoveLatest(owner, colony);
}
