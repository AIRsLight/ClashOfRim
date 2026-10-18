using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Modules.Admin;
using AIRsLight.ClashOfRim.Network.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotRecovery(ColonyDeletionService deletions, AdminConfigurationChanges configuration,
    WorldLayerNotifications layers, TradeLifecycle trades, ColonyDeletion colonies, DiplomacyPublications diplomacy,
    SnapshotCommitService commits, IColonySnapshotIndexStore snapshots, BankLoanRegistry bank, SnapshotPostUploads postUploads,
    IAuthoritativeEventLedger ledger, EventNotificationHub notifications, EventAchievementRecorder achievements,
    ServerShopRegistry shop, MercenaryContractRegistry mercenaries, MercenaryGuardContractRegistry guards,
    PlayerRegistry players, SnapshotConfirmationPlanner confirmations, MercenaryIncidentRecovery incidents) : ISnapshotRecovery
{
    public void RecoverPrerequisites()
    {
        if (deletions.IsApplying)
            throw new InvalidOperationException("Cannot begin nested mutations while a colony deletion is being applied.");
        configuration.Recover();
        layers.Recover();
        trades.RecoverAdminBaselineTradeInvalidations();
        colonies.Recover();
        diplomacy.Recover();
    }

    public ISnapshotRecoveryPreparation CapturePreparation() => commits.CaptureRecoveryPreparation(snapshots);

    public void Replay()
    {
        commits.Recover(snapshots, bank, postUploads.AfterCommit, ledger,
            userId => notifications.SignalUsers(new[] { userId }),
            item => achievements.Record(item, item.AppliedAtUtc ?? item.CreatedAtUtc), shop,
            mercenaries, guards, players, postUploads.AfterOrdinaryUpload, confirmations.Plan);
        incidents.Recover();
    }
}
