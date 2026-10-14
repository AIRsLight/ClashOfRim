using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

// Stores the exact pre-commit records, so recovery never confirms work created after this snapshot.
public sealed record PendingSnapshotOperations(BankLoanRecord? Loan, IReadOnlyList<BankDebtRecord> Debts,
    IReadOnlyList<MercenaryContractRecord> Mercenaries)
{
    public bool IsEmpty => Loan is null && Debts.Count == 0 && Mercenaries.Count == 0;

    internal static PendingSnapshotOperations Capture(BankLoanRegistry bank, MercenaryContractRegistry? contracts,
        LatestSnapshotRecord snapshot, string? excludeLoanId = null, string? excludeDebtId = null)
    {
        var identity = snapshot.Identity;
        var batch = bank.CapturePendingOperations(identity.OwnerId!, identity.ColonyId!, excludeLoanId, excludeDebtId);
        return batch with { Mercenaries = contracts?.CapturePendingOperations(identity.OwnerId!, identity.ColonyId!) ?? [] };
    }

    internal void Validate(LatestSnapshotRecord snapshot)
    {
        bool Owner(string user, string colony) => user == snapshot.Identity.OwnerId && colony == snapshot.Identity.ColonyId;
        if (Debts is null || Mercenaries is null || IsEmpty
            || Loan is { } loan && (!Owner(loan.UserId, loan.ColonyId) || loan.Status is not (BankLoanRegistry.StatusPendingActivation or BankLoanRegistry.StatusPendingRepayment))
            || Debts.Any(debt => !Owner(debt.UserId, debt.ColonyId) || debt.Status is not (BankLoanRegistry.StatusPendingActivation or BankLoanRegistry.StatusPendingRepayment))
            || Mercenaries.Any(contract => !Owner(contract.UserId, contract.ColonyId) || contract.Status != MercenaryContractRegistry.StatusPendingActivation)
            || Debts.Select(debt => debt.DebtId).Distinct(StringComparer.Ordinal).Count() != Debts.Count
            || Mercenaries.Select(contract => contract.ContractId).Distinct(StringComparer.Ordinal).Count() != Mercenaries.Count)
            throw new InvalidDataException("Pending operation journal does not match its confirmed snapshot.");
    }

    internal static BankLoanRecord Confirm(BankLoanRecord loan, LatestSnapshotRecord snapshot) => loan.Status == BankLoanRegistry.StatusPendingActivation
        ? loan with { Status = BankLoanRegistry.StatusActive, SnapshotId = snapshot.Identity.SnapshotId! }
        : loan with { Status = BankLoanRegistry.StatusRepaid, SnapshotId = snapshot.Identity.SnapshotId!, RepaidAtUtc = snapshot.AcceptedAtUtc,
            RepaidAtGameTicks = snapshot.Envelope.GameTicks ?? loan.RepaymentRequestedAtGameTicks };

    internal static BankDebtRecord Confirm(BankDebtRecord debt, LatestSnapshotRecord snapshot) => debt.Status == BankLoanRegistry.StatusPendingActivation
        ? debt with { Status = BankLoanRegistry.StatusActive, SnapshotId = snapshot.Identity.SnapshotId! }
        : debt with { Status = BankLoanRegistry.StatusRepaid, SnapshotId = snapshot.Identity.SnapshotId!, PaidAtUtc = snapshot.AcceptedAtUtc,
            PaidAtGameTicks = snapshot.Envelope.GameTicks ?? debt.RepaymentRequestedAtGameTicks };

    internal static MercenaryContractRecord Confirm(MercenaryContractRecord contract, LatestSnapshotRecord snapshot) =>
        contract with { Status = MercenaryContractRegistry.StatusActive, SnapshotId = snapshot.Identity.SnapshotId!, ActivatedAtUtc = snapshot.AcceptedAtUtc };
}
