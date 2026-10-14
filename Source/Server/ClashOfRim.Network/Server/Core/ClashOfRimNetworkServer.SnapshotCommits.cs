using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    internal static void RecoverSnapshotCommits(ClashOfRimNetworkState state)
    {
        using (state.EnterSnapshotMutation())
        {
            state.SnapshotCommits.Recover(state.SnapshotStore, state.BankLoans,
                (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind), state.Ledger,
                userId => state.EventNotifications.SignalUsers(new[] { userId }),
                item => RecordAuthoritativeEventAchievements(state, item, item.CreatedAtUtc), state.ServerShop,
                state.MercenaryContracts, state.MercenaryGuards, state.Players);
            RecoverMercenaryIncidents(state);
        }
    }

    private static void RecoverMercenaryIncidents(ClashOfRimNetworkState state) =>
        MercenaryIncidentRecovery.Recover(state.BankLoans, state.MercenaryContracts, state.Ledger,
            debt => CreateBankFineNotification(state, debt), user => state.EventNotifications.SignalUser(user));

    private static void RunCommittedSnapshotPostUpload(ClashOfRimNetworkState state, LatestSnapshotRecord snapshot, string? kind)
    {
        RunSnapshotPostUploadProcessors(state, snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, null,
            SnapshotUploadResult.Accept(snapshot, kind), snapshot.AcceptedAtUtc);
    }
}
