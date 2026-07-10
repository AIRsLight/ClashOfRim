using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class BankLoanRegistry
{
    public const string StatusPendingActivation = "PendingActivation";
    public const string StatusActive = "Active";
    public const string StatusPendingRepayment = "PendingRepayment";
    public const string StatusRepaid = "Repaid";
    public const string DebtSourceFine = "Fine";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly Dictionary<string, BankLoanRecord> loans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BankDebtRecord> debts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeLoanIdByColony = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> debtIdByIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<BankDebtRecord>> openDebtsByColony = new(StringComparer.Ordinal);

    public BankLoanRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal BankLoanRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal BankLoanRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
        Load();
    }

    public BankLoanRecord? GetActive(string userId, string colonyId)
    {
        lock (gate)
        {
            return activeLoanIdByColony.TryGetValue(ColonyKey(userId, colonyId), out string? loanId)
                && loans.TryGetValue(loanId, out BankLoanRecord? loan)
                && IsOpenStatus(loan.Status)
                    ? loan
                    : null;
        }
    }

    public BankLoanRecord? FindByIdempotencyKey(string idempotencyKey)
    {
        lock (gate)
        {
            return idByIdempotencyKey.TryGetValue(idempotencyKey, out string? loanId)
                && loans.TryGetValue(loanId, out BankLoanRecord? loan)
                    ? loan
                    : null;
        }
    }

    public IReadOnlyList<BankDebtRecord> GetOpenDebts(string userId, string colonyId)
    {
        lock (gate)
        {
            string colonyKey = ColonyKey(userId, colonyId);
            if (openDebtsByColony.TryGetValue(colonyKey, out IReadOnlyList<BankDebtRecord>? cached))
            {
                return cached;
            }

            IReadOnlyList<BankDebtRecord> result = debts.Values
                .Where(debt => string.Equals(debt.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(debt.ColonyId, colonyId, StringComparison.Ordinal)
                    && IsDebtOpenStatus(debt.Status))
                .OrderBy(debt => debt.CreatedAtUtc)
                .ToList();
            openDebtsByColony[colonyKey] = result;
            return result;
        }
    }

    public BankDebtRecord? FindDebtByIdempotencyKey(string idempotencyKey)
    {
        lock (gate)
        {
            return debtIdByIdempotencyKey.TryGetValue(idempotencyKey, out string? debtId)
                && debts.TryGetValue(debtId, out BankDebtRecord? debt)
                    ? debt
                    : null;
        }
    }

    public BankDebtRecord CreateDebt(
        string idempotencyKey,
        string userId,
        string colonyId,
        string snapshotId,
        int amountSilver,
        string sourceKind,
        string reason,
        string sourceId,
        long createdAtGameTicks,
        DateTimeOffset createdAtUtc,
        bool pendingActivation = false)
    {
        lock (gate)
        {
            if (debtIdByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingDebtId)
                && debts.TryGetValue(existingDebtId, out BankDebtRecord? existing))
            {
                return existing;
            }

            string debtId = "bankdebt-" + Guid.NewGuid().ToString("N");
            var debt = new BankDebtRecord(
                debtId,
                idempotencyKey,
                userId,
                colonyId,
                snapshotId,
                Math.Max(0, amountSilver),
                string.IsNullOrWhiteSpace(sourceKind) ? DebtSourceFine : sourceKind,
                reason,
                sourceId,
                createdAtGameTicks,
                createdAtUtc,
                pendingActivation ? StatusPendingActivation : StatusActive,
                PaidAtUtc: null,
                PaidAtGameTicks: null,
                RepaymentIdempotencyKey: null,
                RepaymentRequestedAtUtc: null,
                RepaymentRequestedAtGameTicks: null);
            debts[debtId] = debt;
            RegisterDebtIdempotencyKeys(debt);
            InvalidateOpenDebtCache(debt);
            SaveLocked();
            return debt;
        }
    }

    public BankDebtRecord? FindDebt(string userId, string colonyId, string debtId)
    {
        lock (gate)
        {
            return debts.TryGetValue(debtId, out BankDebtRecord? debt)
                && string.Equals(debt.UserId, userId, StringComparison.Ordinal)
                && string.Equals(debt.ColonyId, colonyId, StringComparison.Ordinal)
                    ? debt
                    : null;
        }
    }

    public BankDebtRecord? MarkDebtRepaymentPending(
        string idempotencyKey,
        string userId,
        string colonyId,
        string debtId,
        long requestedAtGameTicks,
        DateTimeOffset requestedAtUtc)
    {
        lock (gate)
        {
            if (debtIdByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingDebtId)
                && debts.TryGetValue(existingDebtId, out BankDebtRecord? existing))
            {
                return existing;
            }

            if (!debts.TryGetValue(debtId, out BankDebtRecord? debt)
                || !string.Equals(debt.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(debt.ColonyId, colonyId, StringComparison.Ordinal)
                || !IsDebtOpenStatus(debt.Status))
            {
                return null;
            }

            BankDebtRecord updated = debt with
            {
                Status = StatusPendingRepayment,
                RepaymentIdempotencyKey = idempotencyKey,
                RepaymentRequestedAtUtc = requestedAtUtc,
                RepaymentRequestedAtGameTicks = requestedAtGameTicks
            };
            debts[debtId] = updated;
            RegisterDebtIdempotencyKeys(updated);
            InvalidateOpenDebtCache(updated);
            SaveLocked();
            return updated;
        }
    }

    public BankDebtRecord? MarkDebtRepaidWithSnapshot(
        string idempotencyKey,
        string userId,
        string colonyId,
        string debtId,
        string snapshotId,
        int silverPaid,
        long paidAtGameTicks,
        DateTimeOffset paidAtUtc)
    {
        lock (gate)
        {
            if (debtIdByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingDebtId)
                && debts.TryGetValue(existingDebtId, out BankDebtRecord? existing))
            {
                return existing;
            }

            if (!debts.TryGetValue(debtId, out BankDebtRecord? debt)
                || !string.Equals(debt.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(debt.ColonyId, colonyId, StringComparison.Ordinal)
                || !string.Equals(debt.Status, StatusActive, StringComparison.Ordinal)
                || silverPaid < debt.AmountSilver)
            {
                return null;
            }

            BankDebtRecord updated = debt with
            {
                Status = StatusRepaid,
                SnapshotId = snapshotId,
                PaidAtUtc = paidAtUtc,
                PaidAtGameTicks = paidAtGameTicks,
                RepaymentIdempotencyKey = idempotencyKey,
                RepaymentRequestedAtUtc = paidAtUtc,
                RepaymentRequestedAtGameTicks = paidAtGameTicks
            };
            debts[debtId] = updated;
            RegisterDebtIdempotencyKeys(updated);
            InvalidateOpenDebtCache(updated);
            SaveLocked();
            return updated;
        }
    }

    public BankLoanRecord Create(
        string idempotencyKey,
        string userId,
        string colonyId,
        string snapshotId,
        int principalSilver,
        int interestSilver,
        int durationDays,
        long createdAtGameTicks,
        DateTimeOffset createdAtUtc,
        string? requestedLoanId = null)
    {
        lock (gate)
        {
            if (idByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingLoanId)
                && loans.TryGetValue(existingLoanId, out BankLoanRecord? existing))
            {
                return existing;
            }

            string loanId = string.IsNullOrWhiteSpace(requestedLoanId)
                ? "bankloan-" + Guid.NewGuid().ToString("N")
                : requestedLoanId!;
            if (loans.ContainsKey(loanId))
            {
                loanId = "bankloan-" + Guid.NewGuid().ToString("N");
            }
            long dueAtGameTicks = createdAtGameTicks + durationDays * BankLoanPolicy.GameTicksPerDay;
            var loan = new BankLoanRecord(
                loanId,
                idempotencyKey,
                userId,
                colonyId,
                snapshotId,
                principalSilver,
                interestSilver,
                principalSilver + interestSilver,
                durationDays,
                createdAtGameTicks,
                dueAtGameTicks,
                StatusPendingActivation,
                createdAtUtc,
                RepaidAtUtc: null,
                RepaidAtGameTicks: null,
                RepaymentIdempotencyKey: null,
                RepaymentRequestedAtUtc: null,
                RepaymentRequestedAtGameTicks: null);

            loans[loanId] = loan;
            idByIdempotencyKey[idempotencyKey] = loanId;
            activeLoanIdByColony[ColonyKey(userId, colonyId)] = loanId;
            SaveLocked();
            return loan;
        }
    }

    public BankLoanRecord? MarkRepaymentPending(
        string idempotencyKey,
        string userId,
        string colonyId,
        long requestedAtGameTicks,
        DateTimeOffset requestedAtUtc)
    {
        lock (gate)
        {
            if (idByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingLoanId)
                && loans.TryGetValue(existingLoanId, out BankLoanRecord? existing))
            {
                return existing;
            }

            string colonyKey = ColonyKey(userId, colonyId);
            if (!activeLoanIdByColony.TryGetValue(colonyKey, out string? loanId)
                || !loans.TryGetValue(loanId, out BankLoanRecord? loan))
            {
                return null;
            }

            BankLoanRecord updated = loan with
            {
                Status = StatusPendingRepayment,
                RepaymentIdempotencyKey = idempotencyKey,
                RepaymentRequestedAtUtc = requestedAtUtc,
                RepaymentRequestedAtGameTicks = requestedAtGameTicks
            };
            loans[loanId] = updated;
            idByIdempotencyKey[idempotencyKey] = loanId;
            SaveLocked();
            return updated;
        }
    }

    public BankLoanRecord? MarkRepaidWithSnapshot(
        string idempotencyKey,
        string userId,
        string colonyId,
        string loanId,
        string snapshotId,
        int silverPaid,
        long paidAtGameTicks,
        DateTimeOffset paidAtUtc)
    {
        lock (gate)
        {
            if (idByIdempotencyKey.TryGetValue(idempotencyKey, out string? existingLoanId)
                && loans.TryGetValue(existingLoanId, out BankLoanRecord? existing))
            {
                return existing;
            }

            string colonyKey = ColonyKey(userId, colonyId);
            if (!activeLoanIdByColony.TryGetValue(colonyKey, out string? activeLoanId)
                || !loans.TryGetValue(activeLoanId, out BankLoanRecord? loan)
                || !string.Equals(loan.LoanId, loanId, StringComparison.Ordinal)
                || !string.Equals(loan.Status, StatusActive, StringComparison.Ordinal)
                || silverPaid < loan.TotalDueSilver)
            {
                return null;
            }

            BankLoanRecord updated = loan with
            {
                Status = StatusRepaid,
                SnapshotId = snapshotId,
                RepaidAtUtc = paidAtUtc,
                RepaidAtGameTicks = paidAtGameTicks,
                RepaymentIdempotencyKey = idempotencyKey,
                RepaymentRequestedAtUtc = paidAtUtc,
                RepaymentRequestedAtGameTicks = paidAtGameTicks
            };
            loans[activeLoanId] = updated;
            idByIdempotencyKey[idempotencyKey] = activeLoanId;
            activeLoanIdByColony.Remove(colonyKey);
            SaveLocked();
            return updated;
        }
    }

    public BankConfirmationResult ConfirmPendingForSnapshot(
        string userId,
        string colonyId,
        string acceptedSnapshotId,
        long? acceptedSnapshotGameTicks,
        DateTimeOffset confirmedAtUtc)
    {
        lock (gate)
        {
            string colonyKey = ColonyKey(userId, colonyId);
            BankLoanRecord? confirmedLoan = null;
            if (activeLoanIdByColony.TryGetValue(colonyKey, out string? loanId)
                && loans.TryGetValue(loanId, out BankLoanRecord? loan))
            {
                if (string.Equals(loan.Status, StatusPendingActivation, StringComparison.Ordinal))
                {
                    confirmedLoan = loan with
                    {
                        Status = StatusActive,
                        SnapshotId = acceptedSnapshotId
                    };
                    loans[loanId] = confirmedLoan;
                }
                else if (string.Equals(loan.Status, StatusPendingRepayment, StringComparison.Ordinal))
                {
                    confirmedLoan = loan with
                    {
                        Status = StatusRepaid,
                        SnapshotId = acceptedSnapshotId,
                        RepaidAtUtc = confirmedAtUtc,
                        RepaidAtGameTicks = acceptedSnapshotGameTicks ?? loan.RepaymentRequestedAtGameTicks
                    };
                    loans[loanId] = confirmedLoan;
                    activeLoanIdByColony.Remove(colonyKey);
                }
            }

            List<BankDebtRecord> confirmedDebts = new();
            foreach (BankDebtRecord debt in debts.Values
                         .Where(debt => string.Equals(debt.UserId, userId, StringComparison.Ordinal)
                             && string.Equals(debt.ColonyId, colonyId, StringComparison.Ordinal)
                             && debt.Status is StatusPendingActivation or StatusPendingRepayment)
                         .ToList())
            {
                BankDebtRecord confirmed = string.Equals(debt.Status, StatusPendingActivation, StringComparison.Ordinal)
                    ? debt with
                    {
                        Status = StatusActive,
                        SnapshotId = acceptedSnapshotId
                    }
                    : debt with
                    {
                        Status = StatusRepaid,
                        SnapshotId = acceptedSnapshotId,
                        PaidAtUtc = confirmedAtUtc,
                        PaidAtGameTicks = acceptedSnapshotGameTicks ?? debt.RepaymentRequestedAtGameTicks
                };
                debts[debt.DebtId] = confirmed;
                InvalidateOpenDebtCache(confirmed);
                confirmedDebts.Add(confirmed);
            }

            if (confirmedLoan is not null || confirmedDebts.Count > 0)
            {
                SaveLocked();
            }

            return new BankConfirmationResult(confirmedLoan, confirmedDebts);
        }
    }

    public BankPendingConfirmationReconciliationResult ReconcilePendingConfirmations(
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        TimeSpan timeout,
        bool forceCancel)
    {
        lock (gate)
        {
            bool changed = false;
            int cancelledLoanActivations = 0;
            int cancelledDebtActivations = 0;
            int revertedLoanRepayments = 0;
            int revertedDebtRepayments = 0;
            string colonyKey = ColonyKey(userId, colonyId);
            if (activeLoanIdByColony.TryGetValue(colonyKey, out string? loanId)
                && loans.TryGetValue(loanId, out BankLoanRecord? loan))
            {
                if (string.Equals(loan.Status, StatusPendingActivation, StringComparison.Ordinal)
                    && ShouldCancelPending(loan.CreatedAtUtc, nowUtc, timeout, forceCancel))
                {
                    loans.Remove(loanId);
                    idByIdempotencyKey.Remove(loan.IdempotencyKey);
                    activeLoanIdByColony.Remove(colonyKey);
                    cancelledLoanActivations++;
                    changed = true;
                }
                else if (string.Equals(loan.Status, StatusPendingRepayment, StringComparison.Ordinal)
                         && ShouldCancelPending(loan.RepaymentRequestedAtUtc ?? loan.CreatedAtUtc, nowUtc, timeout, forceCancel))
                {
                    BankLoanRecord reverted = loan with
                    {
                        Status = StatusActive,
                        RepaymentIdempotencyKey = null,
                        RepaymentRequestedAtUtc = null,
                        RepaymentRequestedAtGameTicks = null
                    };
                    loans[loanId] = reverted;
                    if (!string.IsNullOrWhiteSpace(loan.RepaymentIdempotencyKey))
                    {
                        idByIdempotencyKey.Remove(loan.RepaymentIdempotencyKey!);
                    }

                    revertedLoanRepayments++;
                    changed = true;
                }
            }

            foreach (BankDebtRecord debt in debts.Values
                         .Where(debt => string.Equals(debt.UserId, userId, StringComparison.Ordinal)
                             && string.Equals(debt.ColonyId, colonyId, StringComparison.Ordinal)
                             && string.Equals(debt.Status, StatusPendingActivation, StringComparison.Ordinal))
                         .ToList())
            {
                if (!ShouldCancelPending(debt.CreatedAtUtc, nowUtc, timeout, forceCancel))
                {
                    continue;
                }

                debts.Remove(debt.DebtId);
                RemoveDebtIdempotencyKeys(debt);
                InvalidateOpenDebtCache(debt);
                cancelledDebtActivations++;
                changed = true;
            }

            foreach (BankDebtRecord debt in debts.Values
                         .Where(debt => string.Equals(debt.UserId, userId, StringComparison.Ordinal)
                             && string.Equals(debt.ColonyId, colonyId, StringComparison.Ordinal)
                             && string.Equals(debt.Status, StatusPendingRepayment, StringComparison.Ordinal))
                         .ToList())
            {
                DateTimeOffset requestedAt = debt.RepaymentRequestedAtUtc ?? debt.CreatedAtUtc;
                if (!ShouldCancelPending(requestedAt, nowUtc, timeout, forceCancel))
                {
                    continue;
                }

                BankDebtRecord reverted = debt with
                {
                    Status = StatusActive,
                    RepaymentIdempotencyKey = null,
                    RepaymentRequestedAtUtc = null,
                    RepaymentRequestedAtGameTicks = null
                };
                debts[debt.DebtId] = reverted;
                RemoveDebtRepaymentIdempotencyKey(debt);
                InvalidateOpenDebtCache(reverted);
                revertedDebtRepayments++;
                changed = true;
            }

            if (changed)
            {
                SaveLocked();
            }

            return new BankPendingConfirmationReconciliationResult(
                cancelledLoanActivations,
                cancelledDebtActivations,
                revertedLoanRepayments,
                revertedDebtRepayments);
        }
    }

    public int RemoveForColony(string userId, string colonyId)
    {
        lock (gate)
        {
            string colonyKey = ColonyKey(userId, colonyId);
            List<string> removed = loans
                .Where(pair => string.Equals(pair.Value.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(pair.Value.ColonyId, colonyId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList();
            List<string> removedDebts = debts
                .Where(pair => string.Equals(pair.Value.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(pair.Value.ColonyId, colonyId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList();
            foreach (string loanId in removed)
            {
                BankLoanRecord loan = loans[loanId];
                loans.Remove(loanId);
                idByIdempotencyKey.Remove(loan.IdempotencyKey);
                if (!string.IsNullOrWhiteSpace(loan.RepaymentIdempotencyKey))
                {
                    idByIdempotencyKey.Remove(loan.RepaymentIdempotencyKey!);
                }
            }

            foreach (string debtId in removedDebts)
            {
                BankDebtRecord debt = debts[debtId];
                debts.Remove(debtId);
                RemoveDebtIdempotencyKeys(debt);
            }

            activeLoanIdByColony.Remove(colonyKey);
            if (removedDebts.Count > 0)
            {
                InvalidateOpenDebtCache(userId, colonyId);
            }

            if (removed.Count > 0 || removedDebts.Count > 0)
            {
                SaveLocked();
            }

            return removed.Count + removedDebts.Count;
        }
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive)
            && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            SaveLocked();
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in structuredPersistence.ReadAll())
        {
            try
            {
                if (pair.Key.StartsWith("loan:", StringComparison.Ordinal))
                {
                    BankLoanRecord? loan = JsonSerializer.Deserialize<BankLoanRecord>(pair.Value, JsonOptions);
                    if (loan is not null)
                    {
                        RegisterLoadedLoan(loan, overwrite: true);
                    }
                }
                else if (pair.Key.StartsWith("debt:", StringComparison.Ordinal))
                {
                    BankDebtRecord? debt = JsonSerializer.Deserialize<BankDebtRecord>(pair.Value, JsonOptions);
                    if (debt is not null)
                    {
                        RegisterLoadedDebt(debt, overwrite: true);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private bool LoadLegacyReadOnly()
    {
        if (legacyPersistence is null)
        {
            return false;
        }

        string? json = legacyPersistence.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            BankLoanRegistryPersistence? persisted =
                JsonSerializer.Deserialize<BankLoanRegistryPersistence>(json, JsonOptions);
            if (persisted?.Loans is null)
            {
                return false;
            }

            bool imported = false;
            foreach (BankLoanRecord loan in persisted.Loans)
            {
                imported |= RegisterLoadedLoan(loan, overwrite: false);
            }

            if (persisted.Debts is not null)
            {
                foreach (BankDebtRecord debt in persisted.Debts)
                {
                    imported |= RegisterLoadedDebt(debt, overwrite: false);
                }
            }

            return imported;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void RegisterDebtIdempotencyKeys(BankDebtRecord debt)
    {
        if (!string.IsNullOrWhiteSpace(debt.IdempotencyKey))
        {
            debtIdByIdempotencyKey[debt.IdempotencyKey] = debt.DebtId;
        }

        if (!string.IsNullOrWhiteSpace(debt.RepaymentIdempotencyKey))
        {
            debtIdByIdempotencyKey[debt.RepaymentIdempotencyKey!] = debt.DebtId;
        }
    }

    private void RemoveDebtIdempotencyKeys(BankDebtRecord debt)
    {
        if (!string.IsNullOrWhiteSpace(debt.IdempotencyKey))
        {
            debtIdByIdempotencyKey.Remove(debt.IdempotencyKey);
        }

        RemoveDebtRepaymentIdempotencyKey(debt);
    }

    private void RemoveDebtRepaymentIdempotencyKey(BankDebtRecord debt)
    {
        if (!string.IsNullOrWhiteSpace(debt.RepaymentIdempotencyKey))
        {
            debtIdByIdempotencyKey.Remove(debt.RepaymentIdempotencyKey!);
        }
    }

    private void InvalidateOpenDebtCache(BankDebtRecord debt)
    {
        InvalidateOpenDebtCache(debt.UserId, debt.ColonyId);
    }

    private void InvalidateOpenDebtCache(string userId, string colonyId)
    {
        openDebtsByColony.Remove(ColonyKey(userId, colonyId));
    }

    private void SaveLocked()
    {
        if (structuredPersistence is not null)
        {
            Dictionary<string, string> rows = new(StringComparer.Ordinal);
            foreach (BankLoanRecord loan in loans.Values.OrderBy(loan => loan.CreatedAtUtc))
            {
                rows[LoanRowKey(loan.LoanId)] = JsonSerializer.Serialize(loan, JsonOptions);
            }

            foreach (BankDebtRecord debt in debts.Values.OrderBy(debt => debt.CreatedAtUtc))
            {
                rows[DebtRowKey(debt.DebtId)] = JsonSerializer.Serialize(debt, JsonOptions);
            }

            structuredPersistence.ReplaceAll(rows);
            return;
        }

        if (legacyPersistence is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(
            new BankLoanRegistryPersistence(
                loans.Values
                    .OrderBy(loan => loan.CreatedAtUtc)
                    .ToList(),
                debts.Values
                    .OrderBy(debt => debt.CreatedAtUtc)
                    .ToList()),
            JsonOptions);
        legacyPersistence.Write(json);
    }

    private bool RegisterLoadedLoan(BankLoanRecord loan, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(loan.LoanId)
            || string.IsNullOrWhiteSpace(loan.UserId)
            || string.IsNullOrWhiteSpace(loan.ColonyId)
            || string.IsNullOrWhiteSpace(loan.IdempotencyKey))
        {
            return false;
        }

        if (loans.TryGetValue(loan.LoanId, out BankLoanRecord? existing))
        {
            if (!overwrite)
            {
                return false;
            }

            idByIdempotencyKey.Remove(existing.IdempotencyKey);
            if (!string.IsNullOrWhiteSpace(existing.RepaymentIdempotencyKey))
            {
                idByIdempotencyKey.Remove(existing.RepaymentIdempotencyKey!);
            }

            if (IsOpenStatus(existing.Status))
            {
                activeLoanIdByColony.Remove(ColonyKey(existing.UserId, existing.ColonyId));
            }
        }

        loans[loan.LoanId] = loan;
        idByIdempotencyKey[loan.IdempotencyKey] = loan.LoanId;
        if (!string.IsNullOrWhiteSpace(loan.RepaymentIdempotencyKey))
        {
            idByIdempotencyKey[loan.RepaymentIdempotencyKey!] = loan.LoanId;
        }

        if (IsOpenStatus(loan.Status))
        {
            activeLoanIdByColony[ColonyKey(loan.UserId, loan.ColonyId)] = loan.LoanId;
        }

        return true;
    }

    private bool RegisterLoadedDebt(BankDebtRecord debt, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(debt.DebtId)
            || string.IsNullOrWhiteSpace(debt.UserId)
            || string.IsNullOrWhiteSpace(debt.ColonyId)
            || string.IsNullOrWhiteSpace(debt.IdempotencyKey))
        {
            return false;
        }

        if (debts.TryGetValue(debt.DebtId, out BankDebtRecord? existing))
        {
            if (!overwrite)
            {
                return false;
            }

            RemoveDebtIdempotencyKeys(existing);
            InvalidateOpenDebtCache(existing);
        }

        debts[debt.DebtId] = debt;
        RegisterDebtIdempotencyKeys(debt);
        InvalidateOpenDebtCache(debt);
        return true;
    }

    private static string LoanRowKey(string loanId)
    {
        return "loan:" + loanId;
    }

    private static string DebtRowKey(string debtId)
    {
        return "debt:" + debtId;
    }

    private static string ColonyKey(string userId, string colonyId)
    {
        return userId + "\n" + colonyId;
    }

    private static bool IsOpenStatus(string status)
    {
        return status is StatusPendingActivation or StatusActive or StatusPendingRepayment;
    }

    private static bool IsDebtOpenStatus(string status)
    {
        return status is StatusPendingActivation or StatusActive or StatusPendingRepayment;
    }

    private static bool ShouldCancelPending(
        DateTimeOffset requestedAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan timeout,
        bool forceCancel)
    {
        return forceCancel || timeout <= TimeSpan.Zero || requestedAtUtc <= nowUtc - timeout;
    }

    private sealed record BankLoanRegistryPersistence(
        IReadOnlyList<BankLoanRecord> Loans,
        IReadOnlyList<BankDebtRecord>? Debts = null);
}

public sealed record BankLoanRecord(
    string LoanId,
    string IdempotencyKey,
    string UserId,
    string ColonyId,
    string SnapshotId,
    int PrincipalSilver,
    int InterestSilver,
    int TotalDueSilver,
    int DurationDays,
    long CreatedAtGameTicks,
    long DueAtGameTicks,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RepaidAtUtc,
    long? RepaidAtGameTicks,
    string? RepaymentIdempotencyKey,
    DateTimeOffset? RepaymentRequestedAtUtc,
    long? RepaymentRequestedAtGameTicks);

public sealed record BankDebtRecord(
    string DebtId,
    string IdempotencyKey,
    string UserId,
    string ColonyId,
    string SnapshotId,
    int AmountSilver,
    string SourceKind,
    string Reason,
    string SourceId,
    long CreatedAtGameTicks,
    DateTimeOffset CreatedAtUtc,
    string Status,
    DateTimeOffset? PaidAtUtc,
    long? PaidAtGameTicks,
    string? RepaymentIdempotencyKey,
    DateTimeOffset? RepaymentRequestedAtUtc,
    long? RepaymentRequestedAtGameTicks);

public sealed record BankConfirmationResult(
    BankLoanRecord? Loan,
    IReadOnlyList<BankDebtRecord> Debts);

public sealed record BankPendingConfirmationReconciliationResult(
    int CancelledLoanActivations,
    int CancelledDebtActivations,
    int RevertedLoanRepayments,
    int RevertedDebtRepayments)
{
    public bool Changed => CancelledLoanActivations > 0
        || CancelledDebtActivations > 0
        || RevertedLoanRepayments > 0
        || RevertedDebtRepayments > 0;
}

public static class BankLoanPolicy
{
    public const int GameTicksPerDay = 60000;
    public const int RimWorldYearDays = 60;
}
