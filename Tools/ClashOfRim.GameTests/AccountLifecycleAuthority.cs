using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;

namespace ClashOfRim.GameTests;

// Cross-domain assertions for the account lifecycle, not a second mutation path.
internal sealed class AccountLifecycleAuthority(IColonySnapshotIndexStore snapshots, PlayerRegistry players,
    WorldConfigurationRegistry world, BankLoanRegistry loans, MercenaryContractRegistry mercenaries,
    OfflineAccountRegistry accounts, IAuthoritativeEventLedger ledger)
{
    internal string? SnapshotId(string user, string colony) => snapshots.GetLatest(user, colony)?.Identity.SnapshotId;
    internal void VerifyCancelled(string user, string colony) =>
        Check(snapshots.GetLatest(user, colony) is not null && !players.IsDeleted(user, colony), "Cancelling abandonment changed colony.");
    internal void VerifyAbandoned(string user, string colony)
    {
        Check(players.IsDeleted(user, colony) && snapshots.GetLatest(user, colony) is null, "Abandonment did not delete snapshot and tombstone colony.");
        Check(!world.Current!.PlayerColonySites.Any(s => s.UserId == user && s.ColonyId == colony), "Abandoned site remained registered.");
        Check(loans.GetActive(user, colony) is null && loans.GetOpenDebts(user, colony).Count == 0 && mercenaries.CountOpenForColony(user, colony) == 0,
            "Abandoned colony retained liabilities/contracts.");
        Check(accounts.Authenticate(user, "local-test-password", DateTimeOffset.UtcNow).Accepted, "Abandonment deleted account credentials.");
        Check(world.IsAdministrator("rimtest-A"), "Abandoning owner removed server administration.");
    }
    internal void VerifyOrderCancelled(string tradeId)
    {
        Check(ledger.Find(tradeId)?.Status == ServerEventStatus.Cancelled, "Abandoned seller's order remained executable.");
        var memos = ledger.ListAll().Where(e => e.Payload is TradeEventPayload p && p.TradeId == tradeId && p.Stage == TradeStage.AcceptedMemo).ToArray();
        Check(memos.Length == 1 && memos[0].Status == ServerEventStatus.Cancelled, "Abandoned seller's acceptance memo remained executable.");
    }
    internal void VerifyRebuilt(string user, string previousColony, string colony)
    {
        Check(colony != previousColony && players.IsDeleted(user, previousColony), "Rebuild reused/deleted old generation tombstone.");
        var sites = world.Current!.PlayerColonySites.Where(s => s.UserId == user).ToArray();
        Check(snapshots.GetLatest(user, colony) is not null && sites.Length == 1 && sites[0].ColonyId == colony,
            "Rebuild did not publish exactly one site for the new colony generation.");
    }
}
