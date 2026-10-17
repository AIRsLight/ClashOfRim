using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network;

// Transitional composition only. Domain migrations replace these legacy recovery calls;
// remove this adapter by M9 of docs/partial-module-audit-2026-10-17.md.
internal sealed class LegacySnapshotRecoveryAdapter(ClashOfRimNetworkState state) : ISnapshotRecovery
{
    public void RecoverPrerequisites() => ClashOfRimNetworkServer.RecoverBeforeSnapshotCommits(state);
    public ISnapshotRecoveryPreparation CapturePreparation() => state.SnapshotCommits.CaptureRecoveryPreparation(state.SnapshotStore);
    public void Replay() => ClashOfRimNetworkServer.RecoverSnapshotCommitRecords(state);
}
