using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

internal sealed class SupportSnapshotCommitter(SnapshotCommitService commits, IColonySnapshotIndexStore snapshots,
    BankLoanRegistry loans, IAuthoritativeEventLedger ledger, MercenaryContractRegistry mercenaries,
    EventNotificationHub notifications, ISnapshotCommitEffects effects)
{
    internal void Commit(SaveSnapshotPackage package, SnapshotUploadResult upload, AuthoritativeEvent support) =>
        commits.CommitSupport(snapshots, loans, ledger, package, upload.AcceptedSnapshot!, new SupportSnapshotMutation(support),
            effects.AfterCommit, user => notifications.SignalUser(user), upload.SnapshotUploadKind, mercenaries,
            preparedStoragePayload: upload.PreparedStoragePayload);
}
