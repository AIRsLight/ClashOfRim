using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed partial class BankLoanRegistry
{
    internal PendingSnapshotOperations CapturePendingOperations(string user, string colony, string? excludeLoanId, string? excludeDebtId)
    {
        lock (gate)
        {
            var loan = GetActive(user, colony);
            if (loan?.LoanId == excludeLoanId || loan?.Status is not (StatusPendingActivation or StatusPendingRepayment)) loan = null;
            return new PendingSnapshotOperations(loan, debts.Values.Where(debt => debt.UserId == user && debt.ColonyId == colony
                && debt.DebtId != excludeDebtId && debt.Status is StatusPendingActivation or StatusPendingRepayment).ToArray(), []);
        }
    }

    internal void ValidatePendingOperations(PendingSnapshotOperations batch, LatestSnapshotRecord snapshot)
    {
        lock (gate)
        {
            if (batch.Loan is { } loan && (!loans.TryGetValue(loan.LoanId, out var currentLoan)
                || currentLoan != loan && currentLoan != PendingSnapshotOperations.Confirm(loan, snapshot)))
                throw new InvalidDataException("Pending loan changed after its snapshot commit was prepared.");
            foreach (var debt in batch.Debts)
            {
                var confirmed = PendingSnapshotOperations.Confirm(debt, snapshot);
                if (!debts.TryGetValue(debt.DebtId, out var current) || current != debt
                    && current with { MercenaryIncidentFinalized = confirmed.MercenaryIncidentFinalized } != confirmed)
                    throw new InvalidDataException("Pending debt changed after its snapshot commit was prepared.");
            }
        }
    }

    internal void ApplyPendingOperations(PendingSnapshotOperations batch, LatestSnapshotRecord snapshot)
    {
        lock (gate)
        {
            ValidatePendingOperations(batch, snapshot);
            var loanChanges = batch.Loan is { } loan && loans[loan.LoanId] == loan
                ? new[] { PendingSnapshotOperations.Confirm(loan, snapshot) } : Array.Empty<BankLoanRecord>();
            var debtChanges = batch.Debts.Where(debt => debts[debt.DebtId] == debt)
                .Select(debt => PendingSnapshotOperations.Confirm(debt, snapshot)).ToArray();
            if (loanChanges.Length != 0 || debtChanges.Length != 0) PersistBankRecords(loanChanges, debtChanges, [], []);
        }
    }
}
