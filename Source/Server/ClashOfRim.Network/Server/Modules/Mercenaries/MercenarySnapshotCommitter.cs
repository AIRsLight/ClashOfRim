using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Modules.Mercenaries;

internal sealed class MercenarySnapshotCommitter(SnapshotCommitService commits, IColonySnapshotIndexStore snapshots,
    BankLoanRegistry loans, MercenaryContractRegistry contracts, MercenaryGuardContractRegistry guards, ISnapshotCommitEffects effects)
{
    internal void Commit(SaveSnapshotPackage package, SnapshotUploadResult upload, MercenarySnapshotMutation mutation) =>
        commits.CommitMercenary(snapshots, loans, contracts, guards, package, upload.AcceptedSnapshot!, mutation,
            effects.AfterCommit, upload.SnapshotUploadKind, preparedStoragePayload: upload.PreparedStoragePayload);
}
