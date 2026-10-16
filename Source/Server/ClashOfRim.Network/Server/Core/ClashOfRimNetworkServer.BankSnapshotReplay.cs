using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    // Evaluate one immutable configuration at both new-commit boundaries, after checking for a receipt.
    private static ProtocolResponse? ValidateBankLoanSnapshot(ClashOfRimNetworkState state,
        CreateBankLoanWithSnapshotRequest request, BankRequestContext context, out int interestSilver)
    {
        interestSilver = 0;
        var configuration = state.ServerConfiguration;
        if (!configuration.BankLoansEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Bank.Disabled"));
        if (state.BankLoans.ContainsLoanId(request.RequestedLoanId))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.RequestMissingIdentity"));
        if (state.BankLoans.GetActive(request.UserId, request.ColonyId) is not null)
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.ActiveLoanExists"));
        int maximum = CalculateBankMaxLoanSilver(context.ColonyWealth, configuration);
        if (request.PrincipalSilver < configuration.BankMinLoanSilver || request.PrincipalSilver > maximum)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.InvalidPrincipal",
                ("MIN", configuration.BankMinLoanSilver.ToString(CultureInfo.InvariantCulture)),
                ("MAX", maximum.ToString(CultureInfo.InvariantCulture))));
        if (request.DurationDays < configuration.BankMinDurationDays || request.DurationDays > configuration.BankMaxDurationDays)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.InvalidDuration",
                ("MIN", configuration.BankMinDurationDays.ToString(CultureInfo.InvariantCulture)),
                ("MAX", configuration.BankMaxDurationDays.ToString(CultureInfo.InvariantCulture))));
        interestSilver = CalculateBankInterestSilver(request.PrincipalSilver, request.DurationDays, configuration);
        return request.ExpectedInterestSilver == interestSilver ? null
            : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Bank.InterestMismatch"));
    }

    // Caller holds the snapshot mutation gate, then the bank gate, for both checks.
    private static IResult? CheckCreateBankLoanWithSnapshotReplay(HttpRequest httpRequest, ClashOfRimNetworkState state,
        CreateBankLoanWithSnapshotRequest request, DateTimeOffset nowUtc, out BankRequestContext? context)
    {
        context = null;
        if (!ReauthorizeSnapshotPrincipal(httpRequest, state, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
            return Results.Ok(new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, 0, null));
        context = ReconcileBankRequestContext(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            request.ColonyWealth,
            nowUtc,
            out ProtocolResponse? rejection,
            committedSnapshotId: BankSnapshotReplayPolicy.FindLoanSnapshot(state.BankLoans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: false));
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
            if (BankSnapshotReplayPolicy.FindLoanSnapshot(state.BankLoans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: false) is null
                || existingByKey.LoanId != request.RequestedLoanId
                || existingByKey.PrincipalSilver != request.PrincipalSilver
                || existingByKey.DurationDays != request.DurationDays
                || existingByKey.InterestSilver != request.ExpectedInterestSilver)
                return Results.Ok(new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.RequestMissingIdentity")), null, 0, null));
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
        return null;
    }

    // Caller holds the snapshot mutation gate, then the bank gate, for both checks.
    private static IResult? CheckRepayBankLoanWithSnapshotReplay(HttpRequest httpRequest, ClashOfRimNetworkState state,
        RepayBankLoanWithSnapshotRequest request, DateTimeOffset nowUtc, out BankRequestContext? context)
    {
        context = null;
        if (!ReauthorizeSnapshotPrincipal(httpRequest, state, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
            return Results.Ok(new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, 0, null));
        context = ReconcileBankRequestContext(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection,
            committedSnapshotId: BankSnapshotReplayPolicy.FindLoanSnapshot(state.BankLoans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: true));
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
            if (BankSnapshotReplayPolicy.FindLoanSnapshot(state.BankLoans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId, repayment: true) is null
                || existingByKey.LoanId != request.LoanId || request.SilverPaid < existingByKey.TotalDueSilver)
                return Results.Ok(new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.RequestMissingIdentity")), null, 0, null));
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
        return null;
    }

    // Caller holds the snapshot mutation gate, then the bank gate, for both checks.
    private static IResult? CheckRepayBankDebtWithSnapshotReplay(HttpRequest httpRequest, ClashOfRimNetworkState state,
        RepayBankDebtWithSnapshotRequest request, DateTimeOffset nowUtc, out BankRequestContext? context)
    {
        context = null;
        if (!ReauthorizeSnapshotPrincipal(httpRequest, state, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
            return Results.Ok(new BankDebtResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, 0, null));
        context = ReconcileBankRequestContext(
            state,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            request.AuthToken,
            request.CurrentGameTicks,
            colonyWealth: 0,
            nowUtc,
            out ProtocolResponse? rejection,
            committedSnapshotId: BankSnapshotReplayPolicy.FindDebtSnapshot(state.BankLoans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId));
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
            if (BankSnapshotReplayPolicy.FindDebtSnapshot(state.BankLoans, request.IdempotencyKey, request.UserId, request.ColonyId, request.ConfirmedSnapshot?.SnapshotId) is null
                || existingByKey.DebtId != request.DebtId || request.SilverPaid < existingByKey.AmountSilver)
                return Results.Ok(new BankDebtResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Bank.RequestMissingIdentity")), null, 0, null));
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
        return null;
    }
}
