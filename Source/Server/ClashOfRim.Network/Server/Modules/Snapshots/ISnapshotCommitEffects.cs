using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

// Applied by the existing durable post-upload pipeline, also used during commit replay.
internal interface ISnapshotCommitEffects
{
    void AfterCommit(LatestSnapshotRecord snapshot, string? uploadKind);
}
