namespace AIRsLight.ClashOfRim.Network;

internal static class BankSnapshotReplayPolicy
{
    // This only relaxes the latest-snapshot check for a committed retry; authentication still runs first.
    internal static string? FindLoanSnapshot(BankLoanRegistry bank, string key, string userId, string colonyId,
        string? confirmedSnapshotId, bool repayment)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(confirmedSnapshotId)) return null;
        BankLoanRecord? loan = bank.FindByIdempotencyKey(key);
        return loan is not null && loan.UserId == userId && loan.ColonyId == colonyId
            && loan.SnapshotId == confirmedSnapshotId
            && (repayment ? loan.RepaymentIdempotencyKey == key && loan.Status == BankLoanRegistry.StatusRepaid : loan.IdempotencyKey == key)
                ? loan.SnapshotId : null;
    }

    internal static string? FindDebtSnapshot(BankLoanRegistry bank, string key, string userId, string colonyId,
        string? confirmedSnapshotId)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(confirmedSnapshotId)) return null;
        BankDebtRecord? debt = bank.FindDebtByIdempotencyKey(key);
        return debt is not null && debt.UserId == userId && debt.ColonyId == colonyId
            && debt.SnapshotId == confirmedSnapshotId && debt.RepaymentIdempotencyKey == key
            && debt.Status == BankLoanRegistry.StatusRepaid ? debt.SnapshotId : null;
    }
}
