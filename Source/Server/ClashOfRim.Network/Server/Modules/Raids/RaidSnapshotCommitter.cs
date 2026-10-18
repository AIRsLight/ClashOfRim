using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidSnapshotCommitter(SnapshotCommitService commits, IColonySnapshotIndexStore snapshots,
    BankLoanRegistry loans, IAuthoritativeEventLedger ledger, MercenaryContractRegistry mercenaries,
    MercenaryGuardContractRegistry guards, EventNotificationHub notifications, ISnapshotCommitEffects effects, Func<ILogger> logger)
{
    internal void Commit(SaveSnapshotPackage package, SnapshotUploadResult upload, RaidSnapshotMutation mutation)
    {
        commits.CommitRaid(snapshots, loans, ledger, mercenaries, guards, package, upload.AcceptedSnapshot!, mutation,
            effects.AfterCommit, user => notifications.SignalUser(user), upload.SnapshotUploadKind,
            preparedStoragePayload: upload.PreparedStoragePayload);
        EventDiagnostics.LogAppend(logger(), new LedgerAppendResult(mutation.Raid, true), "raid-start-with-snapshot");
    }
}
