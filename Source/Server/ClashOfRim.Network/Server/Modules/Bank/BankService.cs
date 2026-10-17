using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Globalization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using static AIRsLight.ClashOfRim.Network.Modules.Bank.BankLoanTerms;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;

namespace AIRsLight.ClashOfRim.Network.Modules.Bank;

// Owns bank decisions and commit ordering. HTTP decoding and durable commit/recovery have separate owners.
internal sealed class BankService(
    BankLoanRegistry loans, MercenaryContractRegistry mercenaries,
    IColonySnapshotIndexStore snapshots, SnapshotCommitService commits,
    BankRequestAccess access, SnapshotRequestAuthorization requests, SnapshotTransactions transactions,
    ISnapshotCommitEffects effects, Func<ClashOfRimServerConfiguration> getConfiguration)
{
    internal BankStatusResponse GetBankStatus(GetBankStatusRequest request)
    {
        using var mutation = access.EnterBankRequestMutation(request.UserId, request.ColonyId, request.CurrentSnapshotId,
            request.AuthToken, request.CurrentGameTicks, request.ColonyWealth, out BankRequestContext? context, out ProtocolResponse? rejection);
        if (context is null)
        {
            var configuration = getConfiguration();
            return new BankStatusResponse(
                rejection!,
                Math.Max(0, request.ColonyWealth),
                configuration.BankMinLoanSilver,
                configuration.BankMaxLoanSilver,
                maxLoanSilver: 0,
                configuration.BankMaxLoanWealthRatio,
                configuration.BankBaseAnnualInterestRate,
                configuration.BankMinDurationDays,
                configuration.BankMaxDurationDays,
                configuration.BankLoansEnabled,
                configuration.MercenariesEnabled,
                configuration.MercenaryMinDurationDays,
                configuration.MercenaryMaxDurationDays,
                configuration.BankInterestDurationMultiplierCurve,
                configuration.BankPenaltyIntervalDays,
                configuration.BankPenaltyRaidPointsPerSilver,
                configuration.BankOverduePenaltyStages,
                activeLoan: null);
        }

        return BuildBankStatusResponse(context, ProtocolResponse.Ok(ServerLocalization.Text("Bank.StatusRefreshed")));
    }

    internal BankStatusResponse BuildBankStatusResponse(
        BankRequestContext context,
        ProtocolResponse result)
    {
        ClashOfRimServerConfiguration configuration = getConfiguration();
        return new BankStatusResponse(
            result,
            context.ColonyWealth,
            configuration.BankMinLoanSilver,
            configuration.BankMaxLoanSilver,
            CalculateBankMaxLoanSilver(context.ColonyWealth, configuration),
            configuration.BankMaxLoanWealthRatio,
            configuration.BankBaseAnnualInterestRate,
            configuration.BankMinDurationDays,
            configuration.BankMaxDurationDays,
            configuration.BankLoansEnabled,
            configuration.MercenariesEnabled,
            configuration.MercenaryMinDurationDays,
            configuration.MercenaryMaxDurationDays,
            configuration.BankInterestDurationMultiplierCurve,
            configuration.BankPenaltyIntervalDays,
            configuration.BankPenaltyRaidPointsPerSilver,
            configuration.BankOverduePenaltyStages,
            context.ActiveLoan is null ? null : ToBankLoanSummary(context.ActiveLoan, configuration),
            loans.GetOpenDebts(context.UserId, context.ColonyId)
                .Select(ToBankDebtSummary)
                .ToList());
    }

    internal BankLoanResponse CreateBankLoanWithSnapshot(CreateBankLoanWithSnapshotRequest request, byte[] payload, string? principalToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext context;
        int interestSilver;
        using (var mutation = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
        lock (loans.SnapshotCommitGate)
        {
            if (mutation.Rejection is { } denied) return new BankLoanResponse(denied, null, 0, null);
            var replay = CheckCreateBankLoanWithSnapshotReplay(principalToken, request, nowUtc, out var checkedContext);
            if (replay is not null) return replay;
            context = checkedContext!;
            var validation = ValidateBankLoanSnapshot(request, context, out interestSilver);
            if (validation is not null)
                return new BankLoanResponse(validation, null, 0,
                    BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }

        SnapshotUploadResult upload = transactions.PrepareTransactionSnapshot(
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            payload,
            nowUtc);
        using (var mutation = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
            lock (loans.SnapshotCommitGate)
            {
                if (mutation.Rejection is { } denied) return new BankLoanResponse(denied, null, 0, null);
                nowUtc = DateTimeOffset.UtcNow;
                var replay = CheckCreateBankLoanWithSnapshotReplay(principalToken, request, nowUtc, out var checkedContext);
                if (replay is not null) return replay;
                context = checkedContext!;
                if (!upload.Accepted || upload.AcceptedSnapshot is null)
                {
                    return new BankLoanResponse(
                        ToProtocolResponse(upload),
                        loan: null,
                        silverDelta: 0,
                        BuildBankStatusResponse(context, ProtocolResponse.Ok()));
                }
                var validation = ValidateBankLoanSnapshot(request, context, out interestSilver);
                if (validation is not null)
                    return new BankLoanResponse(validation, null, 0,
                        BuildBankStatusResponse(context, ProtocolResponse.Ok()));
                upload = transactions.CreateSnapshotReceiver().CommitPrepared(
                    new SnapshotUploadContext(request.UserId, request.ColonyId, request.ConfirmedSnapshot.SnapshotId!,
                        null, NormalizeSnapshotUploadKind(request.ConfirmedSnapshot.SnapshotUploadKind)),
                    ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload),
                    upload, storeAccepted: false);
                if (!upload.Accepted || upload.AcceptedSnapshot is null)
                {
                    return new BankLoanResponse(
                        ToProtocolResponse(upload),
                        loan: null,
                        silverDelta: 0,
                        BuildBankStatusResponse(context, ProtocolResponse.Ok()));
                }

                BankLoanRecord confirmedLoan = loans.PrepareActivatedLoan(
                    request.IdempotencyKey, request.UserId, request.ColonyId,
                    upload.AcceptedSnapshot.Identity.SnapshotId!, request.PrincipalSilver, interestSilver,
                    request.DurationDays, context.CurrentGameTicks, nowUtc, request.RequestedLoanId);
                commits.Commit(snapshots, loans,
                    ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload),
                    upload.AcceptedSnapshot, confirmedLoan, null,
                    effects.AfterCommit, upload.SnapshotUploadKind, mercenaries, preparedStoragePayload: upload.PreparedStoragePayload);

                BankRequestContext updatedContext = context with { ActiveLoan = confirmedLoan };
                return new BankLoanResponse(
                    ProtocolResponse.Ok(ServerLocalization.Text("Bank.LoanActivated")),
                    ToBankLoanSummary(confirmedLoan, getConfiguration()),
                    confirmedLoan.PrincipalSilver,
                    BuildBankStatusResponse( updatedContext, ProtocolResponse.Ok()),
                    upload.AcceptedSnapshot.Identity.SnapshotId,
                    upload.AcceptedSnapshot.Envelope.NextLineageToken);
            }
    }

    internal BankLoanResponse RepayBankLoanWithSnapshot(RepayBankLoanWithSnapshotRequest request, byte[] payload, string? principalToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext context;
        BankLoanRecord? active;
        using (var mutation = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
        lock (loans.SnapshotCommitGate)
        {
            if (mutation.Rejection is { } denied) return new BankLoanResponse(denied, null, 0, null);
            var replay = CheckRepayBankLoanWithSnapshotReplay(principalToken, request, nowUtc, out var checkedContext);
            if (replay is not null) return replay;
            context = checkedContext!;
            active = loans.GetActive(request.UserId, request.ColonyId);
            if (active is null
                || !string.Equals(active.LoanId, request.LoanId, StringComparison.Ordinal))
            {
                return new BankLoanResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerLocalization.Text("Bank.LoanNotFound")),
                    loan: null,
                    silverDelta: 0,
                    BuildBankStatusResponse(context, ProtocolResponse.Ok()));
            }

            if (!string.Equals(active.Status, BankLoanRegistry.StatusActive, StringComparison.Ordinal))
            {
                return new BankLoanResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.LoanCannotRepay")),
                    ToBankLoanSummary(active, getConfiguration()),
                    silverDelta: 0,
                    BuildBankStatusResponse(context with { ActiveLoan = active }, ProtocolResponse.Ok()));
            }

            if (request.SilverPaid < active.TotalDueSilver)
            {
                return new BankLoanResponse(
                    ProtocolResponse.Reject(
                        ProtocolErrorCode.ValidationFailed,
                        ServerLocalization.Text("Bank.InsufficientSilver", new Dictionary<string, string?> { ["REQUIRED"] = active.TotalDueSilver.ToString(CultureInfo.InvariantCulture), ["SUBMITTED"] = request.SilverPaid.ToString(CultureInfo.InvariantCulture) })),
                    ToBankLoanSummary(active, getConfiguration()),
                    silverDelta: 0,
                    BuildBankStatusResponse(context with { ActiveLoan = active }, ProtocolResponse.Ok()));
            }
        }

        SnapshotUploadResult upload = transactions.PrepareTransactionSnapshot(
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            payload,
            nowUtc);
        using (var mutation = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
            lock (loans.SnapshotCommitGate)
            {
                if (mutation.Rejection is { } denied) return new BankLoanResponse(denied, null, 0, null);
                nowUtc = DateTimeOffset.UtcNow;
                var replay = CheckRepayBankLoanWithSnapshotReplay(principalToken, request, nowUtc, out var checkedContext);
                if (replay is not null) return replay;
                context = checkedContext!;
                if (!upload.Accepted || upload.AcceptedSnapshot is null)
                {
                    return new BankLoanResponse(
                        ToProtocolResponse(upload),
                        loan: null,
                        silverDelta: 0,
                        BuildBankStatusResponse(context with { ActiveLoan = active }, ProtocolResponse.Ok()));
                }
                if (loans.GetActive(request.UserId, request.ColonyId) != active) return new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.LoanCannotRepay")), null, 0, null);
                upload = transactions.CreateSnapshotReceiver().CommitPrepared(
                    new SnapshotUploadContext(request.UserId, request.ColonyId, request.ConfirmedSnapshot.SnapshotId!,
                        null, NormalizeSnapshotUploadKind(request.ConfirmedSnapshot.SnapshotUploadKind)),
                    ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload),
                    upload, storeAccepted: false);
                if (!upload.Accepted || upload.AcceptedSnapshot is null)
                {
                    return new BankLoanResponse(
                        ToProtocolResponse(upload),
                        loan: null,
                        silverDelta: 0,
                        BuildBankStatusResponse(context with { ActiveLoan = active }, ProtocolResponse.Ok()));
                }

                string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!;
                BankLoanRecord repaid = active with
                {
                    Status = BankLoanRegistry.StatusRepaid,
                    SnapshotId = acceptedSnapshotId,
                    RepaidAtUtc = nowUtc,
                    RepaidAtGameTicks = upload.AcceptedSnapshot.Envelope.GameTicks ?? context.CurrentGameTicks,
                    RepaymentIdempotencyKey = request.IdempotencyKey,
                    RepaymentRequestedAtUtc = nowUtc,
                    RepaymentRequestedAtGameTicks = upload.AcceptedSnapshot.Envelope.GameTicks ?? context.CurrentGameTicks
                };
                commits.Commit(snapshots, loans,
                    ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload),
                    upload.AcceptedSnapshot, repaid, null,
                    effects.AfterCommit, upload.SnapshotUploadKind, mercenaries, preparedStoragePayload: upload.PreparedStoragePayload);

                return new BankLoanResponse(
                    ProtocolResponse.Ok(ServerLocalization.Text("Bank.LoanRepaidWithSnapshot")),
                    ToBankLoanSummary(repaid, getConfiguration()),
                    silverDelta: -repaid.TotalDueSilver,
                    BuildBankStatusResponse(context with { ActiveLoan = null }, ProtocolResponse.Ok()),
                    acceptedSnapshotId,
                    upload.AcceptedSnapshot.Envelope.NextLineageToken);
            }
    }

    internal BankDebtResponse RepayBankDebtWithSnapshot(RepayBankDebtWithSnapshotRequest request, byte[] payload, string? principalToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext context;
        BankDebtRecord? debt;
        using (var mutation = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
        lock (loans.SnapshotCommitGate)
        {
            if (mutation.Rejection is { } denied) return new BankDebtResponse(denied, null, 0, null);
            var replay = CheckRepayBankDebtWithSnapshotReplay(principalToken, request, nowUtc, out var checkedContext);
            if (replay is not null) return replay;
            context = checkedContext!;
            debt = loans.FindDebt(request.UserId, request.ColonyId, request.DebtId);
            if (debt is null)
            {
                return new BankDebtResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerLocalization.Text("Bank.DebtNotFound")),
                    debt: null,
                    silverDelta: 0,
                    BuildBankStatusResponse(context, ProtocolResponse.Ok()));
            }

            if (!string.Equals(debt.Status, BankLoanRegistry.StatusActive, StringComparison.Ordinal))
            {
                return new BankDebtResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.DebtCannotRepay")),
                    ToBankDebtSummary(debt),
                    silverDelta: 0,
                    BuildBankStatusResponse(context, ProtocolResponse.Ok()));
            }

            if (request.SilverPaid < debt.AmountSilver)
            {
                return new BankDebtResponse(
                    ProtocolResponse.Reject(
                        ProtocolErrorCode.ValidationFailed,
                        ServerLocalization.Text("Bank.InsufficientSilver", new Dictionary<string, string?> { ["REQUIRED"] = debt.AmountSilver.ToString(CultureInfo.InvariantCulture), ["SUBMITTED"] = request.SilverPaid.ToString(CultureInfo.InvariantCulture) })),
                    ToBankDebtSummary(debt),
                    silverDelta: 0,
                    BuildBankStatusResponse(context, ProtocolResponse.Ok()));
            }
        }

        SnapshotUploadResult upload = transactions.PrepareTransactionSnapshot(
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            payload,
            nowUtc);
        using (var mutation = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
            lock (loans.SnapshotCommitGate)
            {
                if (mutation.Rejection is { } denied) return new BankDebtResponse(denied, null, 0, null);
                nowUtc = DateTimeOffset.UtcNow;
                var replay = CheckRepayBankDebtWithSnapshotReplay(principalToken, request, nowUtc, out var checkedContext);
                if (replay is not null) return replay;
                context = checkedContext!;
                if (!upload.Accepted || upload.AcceptedSnapshot is null)
                {
                    return new BankDebtResponse(
                        ToProtocolResponse(upload),
                        debt: null,
                        silverDelta: 0,
                        BuildBankStatusResponse(context, ProtocolResponse.Ok()));
                }
                if (loans.FindDebt(request.UserId, request.ColonyId, request.DebtId) != debt) return new BankDebtResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.DebtCannotRepay")), null, 0, null);
                upload = transactions.CreateSnapshotReceiver().CommitPrepared(
                    new SnapshotUploadContext(request.UserId, request.ColonyId, request.ConfirmedSnapshot.SnapshotId!,
                        null, NormalizeSnapshotUploadKind(request.ConfirmedSnapshot.SnapshotUploadKind)),
                    ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload),
                    upload, storeAccepted: false);
                if (!upload.Accepted || upload.AcceptedSnapshot is null)
                {
                    return new BankDebtResponse(
                        ToProtocolResponse(upload),
                        debt: null,
                        silverDelta: 0,
                        BuildBankStatusResponse(context, ProtocolResponse.Ok()));
                }

                string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!;
                BankDebtRecord repaid = debt with
                {
                    Status = BankLoanRegistry.StatusRepaid,
                    SnapshotId = acceptedSnapshotId,
                    PaidAtUtc = nowUtc,
                    PaidAtGameTicks = upload.AcceptedSnapshot.Envelope.GameTicks ?? context.CurrentGameTicks,
                    RepaymentIdempotencyKey = request.IdempotencyKey,
                    RepaymentRequestedAtUtc = nowUtc,
                    RepaymentRequestedAtGameTicks = upload.AcceptedSnapshot.Envelope.GameTicks ?? context.CurrentGameTicks
                };
                commits.Commit(snapshots, loans,
                    ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload),
                    upload.AcceptedSnapshot, null, repaid,
                    effects.AfterCommit, upload.SnapshotUploadKind, mercenaries, preparedStoragePayload: upload.PreparedStoragePayload);

                return new BankDebtResponse(
                    ProtocolResponse.Ok(ServerLocalization.Text("Bank.DebtRepaidWithSnapshot")),
                    ToBankDebtSummary(repaid),
                    silverDelta: -repaid.AmountSilver,
                    BuildBankStatusResponse(context, ProtocolResponse.Ok()),
                    acceptedSnapshotId,
                    upload.AcceptedSnapshot.Envelope.NextLineageToken);
            }
    }

    // Evaluate current terms at each new-commit boundary, after checking for an existing receipt.
    private ProtocolResponse? ValidateBankLoanSnapshot(CreateBankLoanWithSnapshotRequest request, BankRequestContext context, out int interestSilver)
    {
        interestSilver = 0;
        var configuration = getConfiguration();
        if (!configuration.BankLoansEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Bank.Disabled"));
        if (loans.ContainsLoanId(request.RequestedLoanId))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.RequestMissingIdentity"));
        if (loans.GetActive(request.UserId, request.ColonyId) is not null)
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.ActiveLoanExists"));
        int maximum = CalculateBankMaxLoanSilver(context.ColonyWealth, configuration);
        if (request.PrincipalSilver < configuration.BankMinLoanSilver || request.PrincipalSilver > maximum)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.InvalidPrincipal", new Dictionary<string, string?>
            {
                ["MIN"] = configuration.BankMinLoanSilver.ToString(CultureInfo.InvariantCulture),
                ["MAX"] = maximum.ToString(CultureInfo.InvariantCulture) }));
        if (request.DurationDays < configuration.BankMinDurationDays || request.DurationDays > configuration.BankMaxDurationDays)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.InvalidDuration", new Dictionary<string, string?>
            {
                ["MIN"] = configuration.BankMinDurationDays.ToString(CultureInfo.InvariantCulture),
                ["MAX"] = configuration.BankMaxDurationDays.ToString(CultureInfo.InvariantCulture) }));
        interestSilver = CalculateBankInterestSilver(request.PrincipalSilver, request.DurationDays, configuration);
        return request.ExpectedInterestSilver == interestSilver ? null
            : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.InterestMismatch"));
    }

    // Receipt checks run under the recovered snapshot scope and the bank commit gate, before and after preparation.
    private BankLoanResponse? CheckCreateBankLoanWithSnapshotReplay(string? principalToken, CreateBankLoanWithSnapshotRequest request, DateTimeOffset nowUtc, out BankRequestContext? context)
    {
        context = null;
        if (!requests.ReauthorizePrincipal(principalToken, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
            return new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerLocalization.Text("Auth.Failed")), null, 0, null);
        context = access.ReadBankRequestContext(
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            request.ColonyWealth,
            nowUtc,
            out ProtocolResponse? rejection,
            committedSnapshotId: BankSnapshotReplayPolicy.FindLoanSnapshot(loans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: false));
        if (context is null)
        {
            return new BankLoanResponse(rejection!, loan: null, silverDelta: 0, status: null);
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.RequestedLoanId)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.LoanTransactionMissingFields")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }

        BankLoanRecord? existingByKey = loans.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            if (BankSnapshotReplayPolicy.FindLoanSnapshot(loans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: false) is null
                || existingByKey.LoanId != request.RequestedLoanId
                || existingByKey.PrincipalSilver != request.PrincipalSilver
                || existingByKey.DurationDays != request.DurationDays
                || existingByKey.InterestSilver != request.ExpectedInterestSilver)
                return new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.RequestMissingIdentity")), null, 0, null);
            string? nextLineageToken = transactions.FindSnapshotNextLineageToken(
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return new BankLoanResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Bank.DuplicateLoanTransaction")),
                ToBankLoanSummary(existingByKey, getConfiguration()),
                existingByKey.PrincipalSilver,
                BuildBankStatusResponse(context with { ActiveLoan = existingByKey }, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken);
        }
        context = access.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
        return rejection is null ? null : new BankLoanResponse(rejection, null, 0,
            BuildBankStatusResponse(context, ProtocolResponse.Ok()));
    }

    private BankLoanResponse? CheckRepayBankLoanWithSnapshotReplay(string? principalToken, RepayBankLoanWithSnapshotRequest request, DateTimeOffset nowUtc, out BankRequestContext? context)
    {
        context = null;
        if (!requests.ReauthorizePrincipal(principalToken, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
            return new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerLocalization.Text("Auth.Failed")), null, 0, null);
        context = access.ReadBankRequestContext(
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection,
            committedSnapshotId: BankSnapshotReplayPolicy.FindLoanSnapshot(loans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: true));
        if (context is null)
        {
            return new BankLoanResponse(rejection!, loan: null, silverDelta: 0, status: null);
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.LoanId)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.RepayLoanTransactionMissingFields")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }

        BankLoanRecord? existingByKey = loans.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            if (BankSnapshotReplayPolicy.FindLoanSnapshot(loans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: true) is null
                || existingByKey.LoanId != request.LoanId || request.SilverPaid < existingByKey.TotalDueSilver)
                return new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.RequestMissingIdentity")), null, 0, null);
            string? nextLineageToken = transactions.FindSnapshotNextLineageToken(
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return new BankLoanResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Bank.DuplicateLoanRepaymentTransaction")),
                ToBankLoanSummary(existingByKey, getConfiguration()),
                silverDelta: -existingByKey.TotalDueSilver,
                BuildBankStatusResponse(context with { ActiveLoan = null }, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken);
        }
        context = access.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
        return rejection is null ? null : new BankLoanResponse(rejection, null, 0,
            BuildBankStatusResponse(context, ProtocolResponse.Ok()));
    }

    private BankDebtResponse? CheckRepayBankDebtWithSnapshotReplay(string? principalToken, RepayBankDebtWithSnapshotRequest request, DateTimeOffset nowUtc, out BankRequestContext? context)
    {
        context = null;
        if (!requests.ReauthorizePrincipal(principalToken, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
            return new BankDebtResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerLocalization.Text("Auth.Failed")), null, 0, null);
        context = access.ReadBankRequestContext(
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection,
            committedSnapshotId: BankSnapshotReplayPolicy.FindDebtSnapshot(loans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId));
        if (context is null)
        {
            return new BankDebtResponse(rejection!, debt: null, silverDelta: 0, status: null);
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.DebtId)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Bank.DebtRepayTransactionMissingFields")),
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }

        BankDebtRecord? existingByKey = loans.FindDebtByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            if (BankSnapshotReplayPolicy.FindDebtSnapshot(loans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId) is null
                || existingByKey.DebtId != request.DebtId || request.SilverPaid < existingByKey.AmountSilver)
                return new BankDebtResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerLocalization.Text("Bank.RequestMissingIdentity")), null, 0, null);
            string? nextLineageToken = transactions.FindSnapshotNextLineageToken(
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return new BankDebtResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Bank.DuplicateDebtRepaymentTransaction")),
                ToBankDebtSummary(existingByKey),
                silverDelta: -existingByKey.AmountSilver,
                BuildBankStatusResponse(context, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken);
        }
        context = access.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
        return rejection is null ? null : new BankDebtResponse(rejection, null, 0,
            BuildBankStatusResponse(context, ProtocolResponse.Ok()));
    }
}
