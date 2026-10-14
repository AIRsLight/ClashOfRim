using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    internal static void RecoverBankSnapshotCommits(ClashOfRimNetworkState state)
    {
        lock (state.RaidSettlementSnapshotMutationGate)
        {
            state.BankSnapshotCommits.Recover(state.SnapshotStore, state.BankLoans,
                (snapshot, kind) => RunBankSnapshotPostUpload(state, snapshot, kind));
        }
    }

    private static void RunBankSnapshotPostUpload(ClashOfRimNetworkState state, LatestSnapshotRecord snapshot, string? kind)
    {
        RunSnapshotPostUploadProcessors(state, snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, null,
            SnapshotUploadResult.Accept(snapshot, kind), snapshot.AcceptedAtUtc);
    }
}
