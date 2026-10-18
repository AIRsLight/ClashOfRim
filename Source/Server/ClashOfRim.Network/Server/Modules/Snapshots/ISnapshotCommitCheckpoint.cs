namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// The coordinator remains the sole owner of journal replacement and the pending intent index.
internal interface ISnapshotCommitCheckpoint
{
    void Persist(SnapshotCommitIntent intent);
}
