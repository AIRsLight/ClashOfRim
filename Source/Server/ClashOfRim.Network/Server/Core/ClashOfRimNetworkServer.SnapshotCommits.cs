using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    internal static void RecoverSnapshotCommits(ClashOfRimNetworkState state)
    {
        lock (state.RaidSettlementSnapshotMutationGate)
        {
            state.SnapshotCommits.Recover(state.SnapshotStore, state.BankLoans,
                (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind), state.Ledger,
                userId => state.EventNotifications.SignalUsers(new[] { userId }),
                item => RecordAuthoritativeEventAchievements(state, item, item.CreatedAtUtc), state.ServerShop);
        }
    }

    private static void RunCommittedSnapshotPostUpload(ClashOfRimNetworkState state, LatestSnapshotRecord snapshot, string? kind)
    {
        RunSnapshotPostUploadProcessors(state, snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, null,
            SnapshotUploadResult.Accept(snapshot, kind), snapshot.AcceptedAtUtc);
    }
}
