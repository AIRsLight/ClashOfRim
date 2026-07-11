using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private const int MercenaryRecallShuttleDestroyedFineSilver = 6000;

    private static IResult GetBankStatus(GetBankStatusRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            request.ColonyWealth,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new BankStatusResponse(
                rejection!,
                Math.Max(0, request.ColonyWealth),
                state.ServerConfiguration.BankMinLoanSilver,
                state.ServerConfiguration.BankMaxLoanSilver,
                maxLoanSilver: 0,
                state.ServerConfiguration.BankMaxLoanWealthRatio,
                state.ServerConfiguration.BankBaseAnnualInterestRate,
                state.ServerConfiguration.BankMinDurationDays,
                state.ServerConfiguration.BankMaxDurationDays,
                state.ServerConfiguration.BankLoansEnabled,
                state.ServerConfiguration.MercenariesEnabled,
                state.ServerConfiguration.MercenaryMinDurationDays,
                state.ServerConfiguration.MercenaryMaxDurationDays,
                state.ServerConfiguration.BankInterestDurationMultiplierCurve,
                state.ServerConfiguration.BankPenaltyIntervalDays,
                state.ServerConfiguration.BankPenaltyRaidPointsPerSilver,
                state.ServerConfiguration.BankOverduePenaltyStages,
                activeLoan: null));
        }

        return Results.Ok(BuildBankStatusResponse(state, context, ProtocolResponse.Ok(T("Bank.StatusRefreshed"))));
    }

    private static IResult CreateBankLoan(CreateBankLoanRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            request.ColonyWealth,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new BankLoanResponse(rejection!, loan: null, silverDelta: 0, status: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.MissingIdempotency")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankLoanRecord? existingByKey = state.BankLoans.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            return Results.Ok(new BankLoanResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Bank.DuplicateLoan")),
                ToBankLoanSummary(existingByKey, state.ServerConfiguration),
                existingByKey.PrincipalSilver,
                BuildBankStatusResponse(state, context with { ActiveLoan = existingByKey }, ProtocolResponse.Ok())));
        }

        if (!state.ServerConfiguration.BankLoansEnabled)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Bank.Disabled")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (state.BankLoans.GetActive(request.UserId, request.ColonyId) is not null)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.ActiveLoanExists")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int maxLoanSilver = CalculateBankMaxLoanSilver(context.ColonyWealth, state.ServerConfiguration);
        if (request.PrincipalSilver < state.ServerConfiguration.BankMinLoanSilver
            || request.PrincipalSilver > maxLoanSilver)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T(
                        "Bank.InvalidPrincipal",
                        ("MIN", state.ServerConfiguration.BankMinLoanSilver.ToString(CultureInfo.InvariantCulture)),
                        ("MAX", maxLoanSilver.ToString(CultureInfo.InvariantCulture)))),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (request.DurationDays < state.ServerConfiguration.BankMinDurationDays
            || request.DurationDays > state.ServerConfiguration.BankMaxDurationDays)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T(
                        "Bank.InvalidDuration",
                        ("MIN", state.ServerConfiguration.BankMinDurationDays.ToString(CultureInfo.InvariantCulture)),
                        ("MAX", state.ServerConfiguration.BankMaxDurationDays.ToString(CultureInfo.InvariantCulture)))),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int interestSilver = CalculateBankInterestSilver(
            request.PrincipalSilver,
            request.DurationDays,
            state.ServerConfiguration);
        BankLoanRecord loan = state.BankLoans.Create(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.PrincipalSilver,
            interestSilver,
            request.DurationDays,
            context.CurrentGameTicks,
            nowUtc);

        BankRequestContext updatedContext = context with { ActiveLoan = loan };
        return Results.Ok(new BankLoanResponse(
            ProtocolResponse.Ok(T("Bank.LoanApprovedPending")),
            ToBankLoanSummary(loan, state.ServerConfiguration),
            loan.PrincipalSilver,
            BuildBankStatusResponse(state, updatedContext, ProtocolResponse.Ok())));
    }

    private static async Task<IResult> CreateBankLoanWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<CreateBankLoanWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<CreateBankLoanWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.LoanTransactionMissingPayload")),
                loan: null,
                silverDelta: 0,
                status: null));
        }

        CreateBankLoanWithSnapshotRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            request.ColonyWealth,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new BankLoanResponse(rejection!, loan: null, silverDelta: 0, status: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.RequestedLoanId)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.LoanTransactionMissingFields")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankLoanRecord? existingByKey = state.BankLoans.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return Results.Ok(new BankLoanResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Bank.DuplicateLoanTransaction")),
                ToBankLoanSummary(existingByKey, state.ServerConfiguration),
                existingByKey.PrincipalSilver,
                BuildBankStatusResponse(state, context with { ActiveLoan = existingByKey }, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken));
        }

        if (!state.ServerConfiguration.BankLoansEnabled)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Bank.Disabled")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (state.BankLoans.GetActive(request.UserId, request.ColonyId) is not null)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.ActiveLoanExists")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int maxLoanSilver = CalculateBankMaxLoanSilver(context.ColonyWealth, state.ServerConfiguration);
        if (request.PrincipalSilver < state.ServerConfiguration.BankMinLoanSilver
            || request.PrincipalSilver > maxLoanSilver)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T(
                        "Bank.InvalidPrincipal",
                        ("MIN", state.ServerConfiguration.BankMinLoanSilver.ToString(CultureInfo.InvariantCulture)),
                        ("MAX", maxLoanSilver.ToString(CultureInfo.InvariantCulture)))),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (request.DurationDays < state.ServerConfiguration.BankMinDurationDays
            || request.DurationDays > state.ServerConfiguration.BankMaxDurationDays)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T(
                        "Bank.InvalidDuration",
                        ("MIN", state.ServerConfiguration.BankMinDurationDays.ToString(CultureInfo.InvariantCulture)),
                        ("MAX", state.ServerConfiguration.BankMaxDurationDays.ToString(CultureInfo.InvariantCulture)))),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int interestSilver = CalculateBankInterestSilver(
            request.PrincipalSilver,
            request.DurationDays,
            state.ServerConfiguration);
        if (request.ExpectedInterestSilver != interestSilver)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.InterestMismatch")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new BankLoanResponse(
                pendingRejection!,
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new BankLoanResponse(
                ToProtocolResponse(upload),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankLoanRecord loan = state.BankLoans.Create(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!,
            request.PrincipalSilver,
            interestSilver,
            request.DurationDays,
            context.CurrentGameTicks,
            nowUtc,
            request.RequestedLoanId);
        BankConfirmationResult confirmation = state.BankLoans.ConfirmPendingForSnapshot(
            request.UserId,
            request.ColonyId,
            upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!,
            upload.AcceptedSnapshot.Envelope.GameTicks,
            nowUtc);
        BankLoanRecord confirmedLoan = confirmation.Loan ?? loan;
        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc);

        BankRequestContext updatedContext = context with { ActiveLoan = confirmedLoan };
        return Results.Ok(new BankLoanResponse(
            ProtocolResponse.Ok(T("Bank.LoanActivated")),
            ToBankLoanSummary(confirmedLoan, state.ServerConfiguration),
            confirmedLoan.PrincipalSilver,
            BuildBankStatusResponse(state, updatedContext, ProtocolResponse.Ok()),
            upload.AcceptedSnapshot.Identity.SnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static async Task<IResult> RepayBankLoanWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<RepayBankLoanWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<RepayBankLoanWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.RepayLoanTransactionMissingPayload")),
                loan: null,
                silverDelta: 0,
                status: null));
        }

        RepayBankLoanWithSnapshotRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new BankLoanResponse(rejection!, loan: null, silverDelta: 0, status: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.LoanId)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.RepayLoanTransactionMissingFields")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankLoanRecord? existingByKey = state.BankLoans.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return Results.Ok(new BankLoanResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Bank.DuplicateLoanRepaymentTransaction")),
                ToBankLoanSummary(existingByKey, state.ServerConfiguration),
                silverDelta: -existingByKey.TotalDueSilver,
                BuildBankStatusResponse(state, context with { ActiveLoan = null }, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken));
        }

        BankLoanRecord? active = state.BankLoans.GetActive(request.UserId, request.ColonyId);
        if (active is null
            || !string.Equals(active.LoanId, request.LoanId, StringComparison.Ordinal))
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Bank.LoanNotFound")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (!string.Equals(active.Status, BankLoanRegistry.StatusActive, StringComparison.Ordinal))
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.LoanCannotRepay")),
                ToBankLoanSummary(active, state.ServerConfiguration),
                silverDelta: 0,
                BuildBankStatusResponse(state, context with { ActiveLoan = active }, ProtocolResponse.Ok())));
        }

        if (request.SilverPaid < active.TotalDueSilver)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Bank.InsufficientSilver", ("REQUIRED", active.TotalDueSilver.ToString(CultureInfo.InvariantCulture)), ("SUBMITTED", request.SilverPaid.ToString(CultureInfo.InvariantCulture)))),
                ToBankLoanSummary(active, state.ServerConfiguration),
                silverDelta: 0,
                BuildBankStatusResponse(state, context with { ActiveLoan = active }, ProtocolResponse.Ok())));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new BankLoanResponse(
                pendingRejection!,
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new BankLoanResponse(
                ToProtocolResponse(upload),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context with { ActiveLoan = active }, ProtocolResponse.Ok())));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!;
        BankLoanRecord? repaid = state.BankLoans.MarkRepaidWithSnapshot(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.LoanId,
            acceptedSnapshotId,
            request.SilverPaid,
            upload.AcceptedSnapshot.Envelope.GameTicks ?? context.CurrentGameTicks,
            nowUtc);
        if (repaid is null)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.LoanRepaymentLedgerFailed")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc);

        return Results.Ok(new BankLoanResponse(
            ProtocolResponse.Ok(T("Bank.LoanRepaidWithSnapshot")),
            ToBankLoanSummary(repaid, state.ServerConfiguration),
            silverDelta: -repaid.TotalDueSilver,
            BuildBankStatusResponse(state, context with { ActiveLoan = null }, ProtocolResponse.Ok()),
            acceptedSnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static IResult RepayBankLoan(RepayBankLoanRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new BankLoanResponse(rejection!, loan: null, silverDelta: 0, status: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.RepayMissingIdempotency")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankLoanRecord? active = state.BankLoans.GetActive(request.UserId, request.ColonyId);
        if (active is null)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Bank.NoUnpaidLoan")),
                loan: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (string.Equals(active.Status, BankLoanRegistry.StatusPendingActivation, StringComparison.Ordinal))
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.LoanPendingActivation")),
                ToBankLoanSummary(active, state.ServerConfiguration),
                silverDelta: 0,
                BuildBankStatusResponse(state, context with { ActiveLoan = active }, ProtocolResponse.Ok())));
        }

        if (string.Equals(active.Status, BankLoanRegistry.StatusPendingRepayment, StringComparison.Ordinal))
        {
            return Results.Ok(new BankLoanResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Bank.RepaymentPending")),
                ToBankLoanSummary(active, state.ServerConfiguration),
                silverDelta: -active.TotalDueSilver,
                BuildBankStatusResponse(state, context with { ActiveLoan = active }, ProtocolResponse.Ok())));
        }

        if (request.SilverPaid < active.TotalDueSilver)
        {
            return Results.Ok(new BankLoanResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Bank.InsufficientSilver", ("REQUIRED", active.TotalDueSilver.ToString(CultureInfo.InvariantCulture)), ("SUBMITTED", request.SilverPaid.ToString(CultureInfo.InvariantCulture)))),
                ToBankLoanSummary(active, state.ServerConfiguration),
                silverDelta: 0,
                BuildBankStatusResponse(state, context with { ActiveLoan = active }, ProtocolResponse.Ok())));
        }

        BankLoanRecord? pendingRepayment = state.BankLoans.MarkRepaymentPending(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            context.CurrentGameTicks,
            nowUtc);

        return Results.Ok(new BankLoanResponse(
            ProtocolResponse.Ok(T("Bank.RepaymentRegistered")),
            pendingRepayment is null ? null : ToBankLoanSummary(pendingRepayment, state.ServerConfiguration),
            silverDelta: -active.TotalDueSilver,
            BuildBankStatusResponse(state, context with { ActiveLoan = pendingRepayment }, ProtocolResponse.Ok())));
    }

    private static async Task<IResult> RepayBankDebtWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<RepayBankDebtWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<RepayBankDebtWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.DebtRepayTransactionMissingPayload")),
                debt: null,
                silverDelta: 0,
                status: null));
        }

        RepayBankDebtWithSnapshotRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new BankDebtResponse(rejection!, debt: null, silverDelta: 0, status: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.DebtId)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.DebtRepayTransactionMissingFields")),
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankDebtRecord? existingByKey = state.BankLoans.FindDebtByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return Results.Ok(new BankDebtResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Bank.DuplicateDebtRepaymentTransaction")),
                ToBankDebtSummary(existingByKey),
                silverDelta: -existingByKey.AmountSilver,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken));
        }

        BankDebtRecord? debt = state.BankLoans.FindDebt(request.UserId, request.ColonyId, request.DebtId);
        if (debt is null)
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Bank.DebtNotFound")),
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (!string.Equals(debt.Status, BankLoanRegistry.StatusActive, StringComparison.Ordinal))
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.DebtCannotRepay")),
                ToBankDebtSummary(debt),
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (request.SilverPaid < debt.AmountSilver)
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Bank.InsufficientSilver", ("REQUIRED", debt.AmountSilver.ToString(CultureInfo.InvariantCulture)), ("SUBMITTED", request.SilverPaid.ToString(CultureInfo.InvariantCulture)))),
                ToBankDebtSummary(debt),
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new BankDebtResponse(
                pendingRejection!,
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new BankDebtResponse(
                ToProtocolResponse(upload),
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!;
        BankDebtRecord? repaid = state.BankLoans.MarkDebtRepaidWithSnapshot(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.DebtId,
            acceptedSnapshotId,
            request.SilverPaid,
            upload.AcceptedSnapshot.Envelope.GameTicks ?? context.CurrentGameTicks,
            nowUtc);
        if (repaid is null)
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.DebtRepaymentLedgerFailed")),
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc);

        return Results.Ok(new BankDebtResponse(
            ProtocolResponse.Ok(T("Bank.DebtRepaidWithSnapshot")),
            ToBankDebtSummary(repaid),
            silverDelta: -repaid.AmountSilver,
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok()),
            acceptedSnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static IResult RepayBankDebt(RepayBankDebtRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new BankDebtResponse(rejection!, debt: null, silverDelta: 0, status: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.DebtId))
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.DebtRepayMissingFields")),
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankDebtRecord? existingByKey = state.BankLoans.FindDebtByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            return Results.Ok(new BankDebtResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Bank.DuplicateDebtRepayment")),
                ToBankDebtSummary(existingByKey),
                silverDelta: -existingByKey.AmountSilver,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankDebtRecord? debt = state.BankLoans.FindDebt(request.UserId, request.ColonyId, request.DebtId);
        if (debt is null)
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Bank.DebtNotFound")),
                debt: null,
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (string.Equals(debt.Status, BankLoanRegistry.StatusRepaid, StringComparison.Ordinal))
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.DebtAlreadyRepaid")),
                ToBankDebtSummary(debt),
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (string.Equals(debt.Status, BankLoanRegistry.StatusPendingActivation, StringComparison.Ordinal))
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.DebtPendingActivation")),
                ToBankDebtSummary(debt),
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (string.Equals(debt.Status, BankLoanRegistry.StatusPendingRepayment, StringComparison.Ordinal))
        {
            return Results.Ok(new BankDebtResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Bank.DebtRepaymentPending")),
                ToBankDebtSummary(debt),
                silverDelta: -debt.AmountSilver,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (request.SilverPaid < debt.AmountSilver)
        {
            return Results.Ok(new BankDebtResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    T("Bank.InsufficientSilver", ("REQUIRED", debt.AmountSilver.ToString(CultureInfo.InvariantCulture)), ("SUBMITTED", request.SilverPaid.ToString(CultureInfo.InvariantCulture)))),
                ToBankDebtSummary(debt),
                silverDelta: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankDebtRecord? pending = state.BankLoans.MarkDebtRepaymentPending(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.DebtId,
            context.CurrentGameTicks,
            nowUtc);

        return Results.Ok(new BankDebtResponse(
            ProtocolResponse.Ok(T("Bank.DebtRepaymentRegistered")),
            pending is null ? null : ToBankDebtSummary(pending),
            silverDelta: -debt.AmountSilver,
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
    }

    private static IResult HireMercenary(HireMercenaryRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new MercenaryHireResponse(rejection!, contract: null, bankStatus: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.Ok(new MercenaryHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.MissingIdempotency")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        MercenaryContractRecord? existingByKey = state.MercenaryContracts.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            return Results.Ok(new MercenaryHireResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Mercenary.DuplicateRequest")),
                ToMercenaryContractDto(existingByKey),
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (!state.ServerConfiguration.MercenariesEnabled)
        {
            return Results.Ok(new MercenaryHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.Disabled")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? validation = ValidateMercenaryRequestShape(
            request.SkillDefName,
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        if (validation is not null)
        {
            return Results.Ok(new MercenaryHireResponse(
                validation,
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? limitRejection = ValidateMercenaryActiveLimit(state, request.UserId, request.ColonyId);
        if (limitRejection is not null)
        {
            return Results.Ok(new MercenaryHireResponse(
                limitRejection,
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int priceSilver = CalculateMercenaryPriceSilver(
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        int deathFineSilver = CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration);
        MercenaryContractRecord contract = state.MercenaryContracts.Create(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.SkillDefName,
            request.SkillLevel,
            request.DurationDays,
            priceSilver,
            state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
            deathFineSilver,
            context.CurrentGameTicks,
            nowUtc);

        return Results.Ok(new MercenaryHireResponse(
            ProtocolResponse.Ok(T("Mercenary.CreatedPending")),
            ToMercenaryContractDto(contract),
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
    }

    private static async Task<IResult> HireMercenaryWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<HireMercenaryWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<HireMercenaryWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new MercenaryHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.TransactionMissingPayload")),
                contract: null,
                bankStatus: null));
        }

        HireMercenaryWithSnapshotRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new MercenaryHireResponse(rejection!, contract: null, bankStatus: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.RequestedContractId)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new MercenaryHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.TransactionMissingFields")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        MercenaryContractRecord? existingByKey = state.MercenaryContracts.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return Results.Ok(new MercenaryHireResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Mercenary.DuplicateTransaction")),
                ToMercenaryContractDto(existingByKey),
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken));
        }

        if (!state.ServerConfiguration.MercenariesEnabled)
        {
            return Results.Ok(new MercenaryHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.Disabled")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? validation = ValidateMercenaryRequestShape(
            request.SkillDefName,
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        if (validation is not null)
        {
            return Results.Ok(new MercenaryHireResponse(
                validation,
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? limitRejection = ValidateMercenaryActiveLimit(state, request.UserId, request.ColonyId);
        if (limitRejection is not null)
        {
            return Results.Ok(new MercenaryHireResponse(
                limitRejection,
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int priceSilver = CalculateMercenaryPriceSilver(
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        int deathFineSilver = CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration);
        if (request.ExpectedPriceSilver != priceSilver
            || request.ExpectedHarmfulSurgeryFineSilver != state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver
            || request.ExpectedDeathFineSilver != deathFineSilver)
        {
            return Results.Ok(new MercenaryHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.QuoteMismatch")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new MercenaryHireResponse(
                pendingRejection!,
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new MercenaryHireResponse(
                ToProtocolResponse(upload),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!;
        MercenaryContractRecord? contract = state.MercenaryContracts.ActivateWithSnapshot(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.RequestedContractId,
            acceptedSnapshotId,
            request.SkillDefName,
            request.SkillLevel,
            request.DurationDays,
            priceSilver,
            state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
            deathFineSilver,
            context.CurrentGameTicks,
            nowUtc);
        if (contract is null)
        {
            return Results.Ok(new MercenaryHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.ContractLedgerFailed")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc);

        return Results.Ok(new MercenaryHireResponse(
            ProtocolResponse.Ok(T("Mercenary.Activated")),
            ToMercenaryContractDto(contract),
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok()),
            acceptedSnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static IResult QuoteMercenary(QuoteMercenaryRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                rejection!,
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                bankStatus: null));
        }

        if (!state.ServerConfiguration.MercenariesEnabled)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.Disabled")),
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? validation = ValidateMercenaryRequestShape(
            request.SkillDefName,
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        if (validation is not null)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                validation,
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? limitRejection = ValidateMercenaryActiveLimit(state, request.UserId, request.ColonyId);
        if (limitRejection is not null)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                limitRejection,
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int priceSilver = CalculateMercenaryPriceSilver(
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        return Results.Ok(new MercenaryQuoteResponse(
            ProtocolResponse.Ok(T("Mercenary.QuoteCreated")),
            request.SkillLevel,
            request.DurationDays,
            priceSilver,
            state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
            CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
    }

    private static IResult QuoteMercenaryGuard(QuoteMercenaryGuardRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        string tier = NormalizeMercenaryGuardTier(request.Tier);
        if (context is null)
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                rejection!,
                tier,
                priceSilver: 0,
                pointRatio: 0,
                bankStatus: null));
        }

        if (!state.ServerConfiguration.PvpEnabled)
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardPvpDisabled")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (!state.ServerConfiguration.MercenariesEnabled || !state.ServerConfiguration.MercenaryGuardsEnabled)
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardDisabled")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (!IsValidMercenaryGuardTier(tier))
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardInvalidTier")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (state.MercenaryGuards.HasActiveForColony(request.UserId, request.ColonyId))
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardAlreadyActive")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        return Results.Ok(new MercenaryGuardQuoteResponse(
            ProtocolResponse.Ok(T("Mercenary.GuardQuoteCreated")),
            tier,
            CalculateMercenaryGuardPriceSilver(tier, state.ServerConfiguration),
            CalculateMercenaryGuardPointRatio(tier, state.ServerConfiguration),
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
    }

    private static async Task<IResult> HireMercenaryGuardWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<HireMercenaryGuardWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<HireMercenaryGuardWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardTransactionMissingPayload")),
                contract: null,
                bankStatus: null));
        }

        HireMercenaryGuardWithSnapshotRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new MercenaryGuardHireResponse(rejection!, contract: null, bankStatus: null));
        }

        string tier = NormalizeMercenaryGuardTier(request.Tier);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.RequestedContractId)
            || string.IsNullOrWhiteSpace(tier)
            || request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardTransactionMissingFields")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        MercenaryGuardContractRecord? existingByKey = state.MercenaryGuards.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingByKey is not null)
        {
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.UserId,
                request.ColonyId,
                existingByKey.SnapshotId);
            return Results.Ok(new MercenaryGuardHireResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Mercenary.GuardDuplicateTransaction")),
                ToMercenaryGuardContractDto(existingByKey),
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok()),
                existingByKey.SnapshotId,
                nextLineageToken));
        }

        if (!state.ServerConfiguration.PvpEnabled)
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardPvpDisabled")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (!state.ServerConfiguration.MercenariesEnabled || !state.ServerConfiguration.MercenaryGuardsEnabled)
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardDisabled")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (!IsValidMercenaryGuardTier(tier))
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardInvalidTier")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (state.MercenaryGuards.HasActiveForColony(request.UserId, request.ColonyId))
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardAlreadyActive")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int priceSilver = CalculateMercenaryGuardPriceSilver(tier, state.ServerConfiguration);
        float pointRatio = CalculateMercenaryGuardPointRatio(tier, state.ServerConfiguration);
        if (request.ExpectedPriceSilver != priceSilver
            || Math.Abs(request.ExpectedPointRatio - pointRatio) > 0.0001f)
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardQuoteMismatch")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                pendingRejection!,
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId!,
            request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ToProtocolResponse(upload),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId ?? request.ConfirmedSnapshot.SnapshotId!;
        MercenaryGuardContractRecord? contract = state.MercenaryGuards.ActivateWithSnapshot(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.RequestedContractId,
            acceptedSnapshotId,
            tier,
            priceSilver,
            pointRatio,
            context.CurrentGameTicks,
            nowUtc);
        if (contract is null)
        {
            return Results.Ok(new MercenaryGuardHireResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardContractLedgerFailed")),
                contract: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc);

        return Results.Ok(new MercenaryGuardHireResponse(
            ProtocolResponse.Ok(T("Mercenary.GuardActivated")),
            ToMercenaryGuardContractDto(contract),
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok()),
            acceptedSnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static IResult ReportMercenaryIncident(ReportMercenaryIncidentRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = ValidateBankRequest(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection);
        if (context is null)
        {
            return Results.Ok(new MercenaryIncidentResponse(rejection!, debt: null, bankStatus: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.ContractId))
        {
            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.IncidentMissingFields")),
                debt: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        if (string.Equals(request.IncidentKind, "Completed", StringComparison.Ordinal))
        {
            MercenaryContractRecord? completionContract = state.MercenaryContracts.Find(request.ContractId);
            if (completionContract is null
                || !string.Equals(completionContract.UserId, request.UserId, StringComparison.Ordinal)
                || !string.Equals(completionContract.ColonyId, request.ColonyId, StringComparison.Ordinal))
            {
                return Results.Ok(new MercenaryIncidentResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Mercenary.ContractNotFound")),
                    debt: null,
                    BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
            }

            if (!string.Equals(completionContract.Status, MercenaryContractRegistry.StatusCompleted, StringComparison.Ordinal))
            {
                if (!string.Equals(completionContract.Status, MercenaryContractRegistry.StatusActive, StringComparison.Ordinal))
                {
                    return Results.Ok(new MercenaryIncidentResponse(
                        ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Mercenary.ContractNotFound")),
                        debt: null,
                        BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
                }

                state.MercenaryContracts.Complete(completionContract.ContractId, request.UserId, request.ColonyId);
            }

            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Ok(T("Mercenary.Completed")),
                debt: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankDebtRecord? existingDebt = state.BankLoans.FindDebtByIdempotencyKey(request.IdempotencyKey);
        if (existingDebt is not null)
        {
            return Results.Ok(new MercenaryIncidentResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Mercenary.DuplicateIncident")),
                ToBankDebtSummary(existingDebt),
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        MercenaryContractRecord? contract = state.MercenaryContracts.FindActive(request.ContractId);
        if (contract is null
            || !string.Equals(contract.UserId, request.UserId, StringComparison.Ordinal)
            || !string.Equals(contract.ColonyId, request.ColonyId, StringComparison.Ordinal))
        {
            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Mercenary.ContractNotFound")),
                debt: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        int fine = request.IncidentKind switch
        {
            "Death" => contract.DeathFineSilver,
            "HostileBehavior" => contract.HarmfulSurgeryFineSilver,
            "HarmfulSurgery" => contract.HarmfulSurgeryFineSilver,
            "OvertimeService" => CalculateMercenaryOvertimeFineSilver(contract),
            "ShuttleDestroyed" => MercenaryRecallShuttleDestroyedFineSilver,
            _ => -1
        };
        if (fine < 0)
        {
            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidIncidentKind")),
                debt: null,
                BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
        }

        BankDebtRecord debt = state.BankLoans.CreateDebt(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            fine,
            BankLoanRegistry.DebtSourceFine,
            "Mercenary:" + request.IncidentKind,
            contract.ContractId,
            context.CurrentGameTicks,
            nowUtc,
            pendingActivation: true);
        CreateBankFineNotification(state, context, debt, nowUtc);

        if (string.Equals(request.IncidentKind, "Death", StringComparison.Ordinal)
            || string.Equals(request.IncidentKind, "ShuttleDestroyed", StringComparison.Ordinal))
        {
            state.MercenaryContracts.Complete(contract.ContractId, request.UserId, request.ColonyId);
        }

        return Results.Ok(new MercenaryIncidentResponse(
            ProtocolResponse.Ok(T("Mercenary.FineCreated")),
            ToBankDebtSummary(debt),
            BuildBankStatusResponse(state, context, ProtocolResponse.Ok())));
    }

    private static void CreateBankFineNotification(
        ClashOfRimNetworkState state,
        BankRequestContext context,
        BankDebtRecord debt,
        DateTimeOffset nowUtc)
    {
        if (!string.Equals(debt.SourceKind, BankLoanRegistry.DebtSourceFine, StringComparison.Ordinal))
        {
            return;
        }

        string notificationId = "bank-fine-created:" + debt.DebtId;
        AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            new EventParty(context.UserId, context.ColonyId),
            notificationId,
            state.OnlinePresence.IsUserOnline(context.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                T("Bank.FineCreatedTitle"),
                T(
                    "Bank.FineCreatedMessage",
                    ("AMOUNT", debt.AmountSilver.ToString(CultureInfo.InvariantCulture))),
                ServerNotificationSeverity.Warning,
                FromAdministrator: false,
                RelatedUserId: context.UserId,
                RelatedColonyId: context.ColonyId),
            nowUtc);
        LedgerAppendResult append = state.Ledger.Append(notification);
        LogEventAppend(state, append, "bank-fine-created");
        if (append.Created)
        {
            state.EventNotifications.SignalUser(context.UserId);
        }
    }

    private static BankRequestContext? ValidateBankRequest(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long requestedCurrentGameTicks,
        int colonyWealth,
        DateTimeOffset nowUtc,
        out ProtocolResponse? rejection)
    {
        rejection = null;
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(currentSnapshotId))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.RequestMissingIdentity"));
            return null;
        }

        if (!IsAuthorizedForColony(
                state,
                authToken,
                userId,
                colonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure);
            return null;
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(userId, colonyId);
        if (latest is null)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Bank.MissingSnapshot"));
            return null;
        }

        if (!string.Equals(latest.Identity.SnapshotId, currentSnapshotId, StringComparison.Ordinal))
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Bank.SnapshotNotLatest"));
            return null;
        }

        long currentGameTicks = requestedCurrentGameTicks > 0
            ? requestedCurrentGameTicks
            : latest.Envelope.GameTicks.GetValueOrDefault();
        if (currentGameTicks <= 0)
        {
            rejection = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.MissingGameTime"));
            return null;
        }

        ReconcilePendingConfirmationsForColony(state, userId, colonyId, nowUtc, forceCancel: false);
        BankLoanRecord? activeLoan = state.BankLoans.GetActive(userId, colonyId);
        return new BankRequestContext(
            userId,
            colonyId,
            currentSnapshotId,
            currentGameTicks,
            Math.Max(0, colonyWealth),
            activeLoan);
    }

    private static BankStatusResponse BuildBankStatusResponse(
        ClashOfRimNetworkState state,
        BankRequestContext context,
        ProtocolResponse result)
    {
        ClashOfRimServerConfiguration configuration = state.ServerConfiguration;
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
            state.BankLoans.GetOpenDebts(context.UserId, context.ColonyId)
                .Select(ToBankDebtSummary)
                .ToList());
    }

    private static BankLoanSummaryDto ToBankLoanSummary(
        BankLoanRecord loan,
        ClashOfRimServerConfiguration configuration)
    {
        return new BankLoanSummaryDto(
            loan.LoanId,
            loan.PrincipalSilver,
            loan.InterestSilver,
            loan.TotalDueSilver,
            loan.DurationDays,
            loan.CreatedAtGameTicks,
            loan.DueAtGameTicks,
            CalculateBankPenaltyRaidPoints(loan.TotalDueSilver, configuration),
            loan.Status);
    }

    private static BankDebtSummaryDto ToBankDebtSummary(BankDebtRecord debt)
    {
        return new BankDebtSummaryDto(
            debt.DebtId,
            debt.AmountSilver,
            debt.SourceKind,
            debt.Reason,
            debt.SourceId,
            debt.CreatedAtGameTicks,
            debt.Status);
    }

    private static ChatMessageDto ToChatMessageDto(ChatMessageRecord message)
    {
        return new ChatMessageDto(
            message.Sequence,
            message.MessageId,
            message.Channel,
            message.FromUserId,
            message.FromColonyId,
            message.TargetUserId,
            message.Text,
            message.SentAtUtc);
    }

    private static MercenaryContractDto ToMercenaryContractDto(MercenaryContractRecord contract)
    {
        return new MercenaryContractDto(
            contract.ContractId,
            contract.SkillDefName,
            contract.SkillLevel,
            contract.DurationDays,
            contract.PriceSilver,
            contract.HarmfulSurgeryFineSilver,
            contract.DeathFineSilver,
            contract.CreatedAtGameTicks,
            contract.ExpiresAtGameTicks);
    }

    private static MercenaryGuardContractDto ToMercenaryGuardContractDto(MercenaryGuardContractRecord contract)
    {
        return new MercenaryGuardContractDto(
            contract.ContractId,
            contract.Tier,
            contract.PriceSilver,
            contract.PointRatio,
            contract.CreatedAtGameTicks);
    }

    private static bool IsAllowedMercenarySkill(string? skillDefName)
    {
        return skillDefName is
            "Construction" or
            "Plants" or
            "Intellectual" or
            "Mining" or
            "Shooting" or
            "Melee" or
            "Social" or
            "Animals" or
            "Cooking" or
            "Medicine" or
            "Artistic" or
            "Crafting" or
            "Hauling";
    }

    private static ProtocolResponse? ValidateMercenaryRequestShape(
        string? skillDefName,
        int skillLevel,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        if (!IsAllowedMercenarySkill(skillDefName))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidSkill"));
        }

        if (skillLevel is not (7 or 14 or 20))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidTier"));
        }

        if (durationDays < configuration.MercenaryMinDurationDays
            || durationDays > configuration.MercenaryMaxDurationDays)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T(
                    "Mercenary.InvalidDuration",
                    ("MIN", configuration.MercenaryMinDurationDays.ToString(CultureInfo.InvariantCulture)),
                    ("MAX", configuration.MercenaryMaxDurationDays.ToString(CultureInfo.InvariantCulture))));
        }

        return null;
    }

    private static int CalculateMercenaryDeathFineSilver(int skillLevel, ClashOfRimServerConfiguration configuration)
    {
        return skillLevel switch
        {
            7 => configuration.MercenaryApprenticeDeathFineSilver,
            14 => configuration.MercenarySkilledDeathFineSilver,
            20 => configuration.MercenaryMasterDeathFineSilver,
            _ => 0
        };
    }

    private static int CalculateMercenaryOvertimeFineSilver(MercenaryContractRecord contract)
    {
        int durationDays = Math.Max(1, contract.DurationDays);
        return Math.Max(1, (int)Math.Ceiling(contract.PriceSilver / (double)durationDays * 3d));
    }

    private static string NormalizeMercenaryGuardTier(string? tier)
    {
        if (string.Equals(tier, "Skilled", StringComparison.OrdinalIgnoreCase))
        {
            return "Skilled";
        }

        if (string.Equals(tier, "Master", StringComparison.OrdinalIgnoreCase))
        {
            return "Master";
        }

        return "Apprentice";
    }

    private static bool IsValidMercenaryGuardTier(string? tier)
    {
        return tier is "Apprentice" or "Skilled" or "Master";
    }

    private static int CalculateMercenaryGuardPriceSilver(
        string tier,
        ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(0, tier switch
        {
            "Skilled" => configuration.MercenaryGuardSkilledSilver,
            "Master" => configuration.MercenaryGuardMasterSilver,
            _ => configuration.MercenaryGuardApprenticeSilver
        });
    }

    private static float CalculateMercenaryGuardPointRatio(
        string tier,
        ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(0f, tier switch
        {
            "Skilled" => configuration.MercenaryGuardSkilledPointsRatio,
            "Master" => configuration.MercenaryGuardMasterPointsRatio,
            _ => configuration.MercenaryGuardApprenticePointsRatio
        });
    }

    private static ProtocolResponse? ValidateMercenaryActiveLimit(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId)
    {
        int limit = state.ServerConfiguration.MaxActiveMercenariesPerColony;
        if (limit <= 0)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T("Mercenary.ActiveLimitReached", ("MAX", "0")));
        }

        int active = state.MercenaryContracts.CountActiveForColony(userId, colonyId);
        return active >= limit
            ? ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T("Mercenary.ActiveLimitReached", ("MAX", limit.ToString(CultureInfo.InvariantCulture))))
            : null;
    }

    private static int CalculateMercenaryPriceSilver(
        int skillLevel,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        int daily = skillLevel switch
        {
            7 => configuration.MercenaryApprenticeDailySilver,
            14 => configuration.MercenarySkilledDailySilver,
            20 => configuration.MercenaryMasterDailySilver,
            _ => 0
        };
        double price = Math.Max(0, daily)
            * Math.Max(0, durationDays)
            * InterpolateBankInterestDurationMultiplier(
                durationDays,
                configuration.MercenaryDurationMultiplierCurve);
        return (int)Math.Ceiling(price);
    }

    private static int CalculateBankMaxLoanSilver(int colonyWealth, ClashOfRimServerConfiguration configuration)
    {
        int wealthBasedLimit = (int)Math.Floor(Math.Max(0, colonyWealth) * configuration.BankMaxLoanWealthRatio);
        return configuration.BankMaxLoanSilver > 0
            ? Math.Min(wealthBasedLimit, configuration.BankMaxLoanSilver)
            : wealthBasedLimit;
    }

    private static int CalculateBankInterestSilver(
        int principalSilver,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        double interest = Math.Max(0, principalSilver)
            * configuration.BankBaseAnnualInterestRate
            * Math.Max(0, durationDays)
            / BankLoanPolicy.RimWorldYearDays
            * InterpolateBankInterestDurationMultiplier(durationDays, configuration.BankInterestDurationMultiplierCurve);
        return (int)Math.Ceiling(interest);
    }

    private static double InterpolateBankInterestDurationMultiplier(
        int durationDays,
        IReadOnlyList<BankInterestDurationMultiplierPointDto>? curve)
    {
        if (curve is null || curve.Count == 0)
        {
            return 1d;
        }

        List<BankInterestDurationMultiplierPointDto> points = curve
            .Where(point => point.DurationDays >= 0 && point.Multiplier >= 0f && !float.IsNaN(point.Multiplier))
            .OrderBy(point => point.DurationDays)
            .ToList();
        if (points.Count == 0)
        {
            return 1d;
        }

        if (durationDays <= points[0].DurationDays)
        {
            return points[0].Multiplier;
        }

        for (int i = 1; i < points.Count; i++)
        {
            BankInterestDurationMultiplierPointDto previous = points[i - 1];
            BankInterestDurationMultiplierPointDto current = points[i];
            if (durationDays > current.DurationDays)
            {
                continue;
            }

            int span = current.DurationDays - previous.DurationDays;
            if (span <= 0)
            {
                return current.Multiplier;
            }

            double t = (double)(durationDays - previous.DurationDays) / span;
            return previous.Multiplier + (current.Multiplier - previous.Multiplier) * t;
        }

        return points[^1].Multiplier;
    }

    private static int CalculateBankPenaltyRaidPoints(int totalDueSilver, ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalDueSilver) * configuration.BankPenaltyRaidPointsPerSilver));
    }

    private sealed record BankRequestContext(
        string UserId,
        string ColonyId,
        string CurrentSnapshotId,
        long CurrentGameTicks,
        int ColonyWealth,
        BankLoanRecord? ActiveLoan);
}
