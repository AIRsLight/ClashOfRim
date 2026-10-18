using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    internal static void RecoverSnapshotCommits(ClashOfRimNetworkState state)
    {
        using var mutation = EnterRecoveredSnapshotMutation(state);
    }

    // Transitional facade for unmigrated domains; the coordinator owns the recovery boundary.
    private static RecoveredSnapshotMutation EnterRecoveredSnapshotMutation(ClashOfRimNetworkState state,
        Func<ProtocolResponse?>? authorize = null, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => state.SnapshotMutations.Enter(authorize, operation);

    internal static void RecoverBeforeSnapshotCommits(ClashOfRimNetworkState state)
    {
        if (state.ColonyDeletions.IsApplying)
            throw new InvalidOperationException("Cannot begin nested mutations while a colony deletion is being applied.");
        state.ConfigurationChanges.Recover();
        state.WorldLayerNotifications.Recover();
        state.TradeLifecycle.RecoverAdminBaselineTradeInvalidations();
        state.ColonyDeletion.Recover();
        state.DiplomacyPublications.Recover();
    }

    internal static void RecoverSnapshotCommitRecords(ClashOfRimNetworkState state)
    {
        state.SnapshotCommits.Recover(state.SnapshotStore, state.BankLoans,
            state.SnapshotPostUploads.AfterCommit, state.Ledger,
            userId => state.EventNotifications.SignalUsers(new[] { userId }),
            item => state.EventAchievements.Record(item, item.AppliedAtUtc ?? item.CreatedAtUtc), state.ServerShop,
            state.MercenaryContracts, state.MercenaryGuards, state.Players,
            state.SnapshotPostUploads.AfterOrdinaryUpload,
            state.SnapshotConfirmations.Plan);
        state.MercenaryIncidents.Recover();
    }
}
