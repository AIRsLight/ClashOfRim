using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

// Transitional composition only. Domain migrations replace these legacy recovery calls;
// remove this adapter by M9 of docs/partial-module-audit-2026-10-17.md.
internal sealed class LegacySnapshotRecoveryAdapter(ClashOfRimNetworkState state) : ISnapshotRecovery, ISnapshotCommitEffects
{
    public void RecoverPrerequisites() => ClashOfRimNetworkServer.RecoverBeforeSnapshotCommits(state);
    public ISnapshotRecoveryPreparation CapturePreparation() => state.SnapshotCommits.CaptureRecoveryPreparation(state.SnapshotStore);
    public void Replay() => ClashOfRimNetworkServer.RecoverSnapshotCommitRecords(state);
    public void AfterCommit(LatestSnapshotRecord snapshot, string? uploadKind) =>
        state.SnapshotPostUploads.AfterCommit(snapshot, uploadKind);
}
