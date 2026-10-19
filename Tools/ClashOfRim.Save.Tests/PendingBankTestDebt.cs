using AIRsLight.ClashOfRim.Network;
using static SnapshotTestAssertions;

internal static class PendingBankTestDebt
{
    public static void Create(BankLoanRegistry bank, string key, DateTimeOffset at) => bank.CreateDebt(key,
        "userA", "colonyA", "before", 50, BankLoanRegistry.DebtSourceFine, "fine", "source", 1000, at, true);

    public static void AddAncillary(BankLoanRegistry bank) => Create(bank, "ancillary", DateTimeOffset.UnixEpoch);

    public static void VerifyAncillary(BankLoanRegistry bank, string snapshotId) =>
        Check(bank.FindDebtByIdempotencyKey("ancillary") is { Status: BankLoanRegistry.StatusActive } debt && debt.SnapshotId == snapshotId,
            "Business snapshot recovery lost its ancillary confirmation batch.");
}
