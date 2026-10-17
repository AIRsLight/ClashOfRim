using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal sealed class TradeSnapshotCommitter(SnapshotCommitService commits, IColonySnapshotIndexStore snapshots,
    BankLoanRegistry loans, IAuthoritativeEventLedger ledger, MercenaryContractRegistry mercenaries,
    EventNotificationHub notifications, ISnapshotCommitEffects effects, IEventAchievementRecorder achievements)
{
    internal void Commit( SaveSnapshotPackage package,
        SnapshotUploadResult upload, TradeSnapshotMutation mutation)
    {
        commits.CommitTrade(snapshots, loans, ledger,
            package, upload.AcceptedSnapshot!, mutation,
            effects.AfterCommit,
            user => notifications.SignalUsers(new[] { user }),
            item => achievements.Record(item, item.CreatedAtUtc), upload.SnapshotUploadKind, mercenaries, preparedStoragePayload: upload.PreparedStoragePayload);
    }

}
