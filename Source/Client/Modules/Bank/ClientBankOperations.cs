using System;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Quests;

namespace AIRsLight.ClashOfRim.Modules.Bank;

// One preparation/submission/retry path for all bank writes. A retry keeps the original
// command, identity, base snapshot and game; it never reapplies silver or quest mutations.
internal sealed class ClientBankOperations
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientBankState state;
    private readonly IClientBankTransport transport;
    private readonly IClientBankRuntime runtime;
    private readonly IClientBankPresentation presentation;

    internal ClientBankOperations(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
    ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery,
    ClientBankState state, IClientBankTransport transport, IClientBankRuntime runtime, IClientBankPresentation presentation)
    {
        this.session = session;
        this.synchronization = synchronization;
        this.mutations = mutations;
        this.transactions = transactions;
        this.recovery = recovery;
        this.state = state;
        this.transport = transport;
        this.runtime = runtime;
        this.presentation = presentation;
    }

    internal event Action? Completed;

    internal Task RefreshAsync()
    {
        using var publication = ClientSessionWork.EnterPublication();
        var attempt = Begin();
        if (attempt is null) return Task.CompletedTask;
        state.Message = Text("StatusRefreshing");
        ClientBankFacts facts;
        try { facts = runtime.ReadFacts(); }
        catch { attempt.Admission.Dispose(); throw; }
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.ReadAsync(attempt.Context, facts.Ticks, facts.Wealth, attempt.Owner.Token), r => r.Result,
            failure => state.Message = FailureText(failure, "StatusRefreshFailed", "StatusRefreshRejected"), response =>
            {
                state.View = response;
                state.Message = presentation.Summary(response);
                transactions.TryApply(attempt, () => runtime.RefreshDebts(response));
            }));
    }

    internal Task BorrowAsync(int principal, int duration) => Start(ClientBankOperation.Borrow, principal, duration, null);
    internal Task RepayLoanAsync() => Start(ClientBankOperation.RepayLoan, 0, 0, null);
    internal Task RepayDebtAsync(string? debtId) => Start(ClientBankOperation.RepayDebt, 0, 0, debtId);

    private Task Start(ClientBankOperation operation, int principal, int duration, string? debtId)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var attempt = Begin();
        if (attempt is null) return Task.CompletedTask;
        bool changed = false;
        try
        {
            var view = state.View;
            var facts = runtime.ReadFacts();
            var command = Plan(operation, principal, duration, debtId, view, facts, attempt);
            if (command is null || !transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
            var pending = ClientBankView.Pending(view!, command);
            mutations.Begin(LocalMutationKind.Bank, Text("StatusUploadingConfirmation"));
            changed = true;
            var applied = runtime.Apply(command, pending);
            if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
            if (!applied.Success)
            {
                // Partial game writes cannot be proven rolled back. Reload authority; do not apply again.
                state.Message = applied.Message;
                recovery.Show(LocalMutationKind.Bank, applied.Message, static () => { }, canRetry: false);
                return Task.CompletedTask;
            }
            state.View = pending;
            return PrepareAndSubmit(attempt, command, pending, null, null);
        }
        catch (Exception ex)
        {
            transactions.TryApply(attempt, () =>
            {
                state.Message = ExceptionText(ex);
                if (changed) recovery.Show(LocalMutationKind.Bank, state.Message, static () => { }, canRetry: false);
                presentation.Diagnose(ex);
            });
            return Task.CompletedTask;
        }
        finally { attempt.Admission.Dispose(); }
    }

    private ClientBankCommand? Plan(ClientBankOperation operation, int principal, int duration, string? debtId,
        ModBankStatusResponseDto? view, ClientBankFacts facts, ClientSnapshotTransaction.Attempt attempt)
    {
        if (view is null) { Reject("StatusNotPulled"); return null; }
        ModBankLoanSummaryDto? loan = null;
        ModBankDebtSummaryDto? debt = null;
        if (operation == ClientBankOperation.Borrow)
        {
            if (view.ActiveLoan is not null) { RejectMessage(presentation.Summary(view)); return null; }
            if (!facts.HasLoanMap) { Reject("StatusNoMap"); return null; }
            int maximum = ClientBankTerms.Maximum(facts.Wealth, view);
            if (!view.BankLoansEnabled) { Reject("StatusLoansDisabled"); return null; }
            if (principal < Math.Max(1, view.MinLoanSilver))
            { RejectMessage(Text("StatusCreateMinimumLoan", ("MIN", Math.Max(1, view.MinLoanSilver)))); return null; }
            if (principal > maximum)
            {
                RejectMessage(Text("StatusCreateRejected", ("CODE", "ValidationFailed"),
                    ("MESSAGE", Text("StatusCreateMaxLoanExceeded", ("MAX", maximum)))));
                return null;
            }
            if (duration < view.MinDurationDays || duration > view.MaxDurationDays)
            {
                RejectMessage(Text("StatusCreateRejected", ("CODE", "ValidationFailed"),
                    ("MESSAGE", Text("StatusCreateDurationInvalid", ("MIN", view.MinDurationDays), ("MAX", view.MaxDurationDays)))));
                return null;
            }
            int interest = ClientBankTerms.CalculateInterestSilver(principal, duration, view);
            loan = new() { LoanId = "bankloan-" + Guid.NewGuid().ToString("N"), PrincipalSilver = principal, InterestSilver = interest,
                TotalDueSilver = principal + interest, DurationDays = duration, CreatedAtGameTicks = facts.Ticks,
                DueAtGameTicks = facts.Ticks + duration * (long)ClashManagedQuestTimingUtility.TicksPerDay,
                PenaltyRaidPoints = Math.Max(1, (int)Math.Ceiling(Math.Max(0, principal + interest) * view.PenaltyRaidPointsPerSilver)),
                Status = "Active", SourceKind = "Loan" };
        }
        else
        {
            if (operation == ClientBankOperation.RepayLoan)
            {
                loan = view.ActiveLoan;
                if (loan is null) { Reject("StatusNoActiveLoan"); return null; }
                loan.TotalDueSilver = Math.Max(loan.TotalDueSilver, runtime.LoanTotalDue(loan.LoanId, loan.TotalDueSilver));
            }
            else
            {
                debt = view.OpenDebts.FirstOrDefault(d => d.DebtId == debtId && d.Status == "Active");
                if (debt is null) { Reject("StatusMissingDebt"); return null; }
            }
            int needed = loan?.TotalDueSilver ?? debt!.AmountSilver;
            if (facts.Silver < needed) { RejectMessage(Text("StatusInsufficientSilver", ("NEEDED", needed), ("AVAILABLE", facts.Silver))); return null; }
        }
        string prefix = operation switch { ClientBankOperation.Borrow => "bank-loan", ClientBankOperation.RepayLoan => "bank-repay", _ => "bank-debt-repay" };
        string key = $"{prefix}:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{attempt.Context.CurrentSnapshotId}:{Guid.NewGuid():N}";
        return new(operation, key, facts.Ticks, facts.Wealth, loan, debt);
    }

    private Task Retry(ClientSnapshotTransaction.Attempt original, ClientBankCommand command, ModBankStatusResponseDto pending,
        ModSnapshotPackageMetadataDto? package, byte[]? payload)
    {
        using var publication = ClientSessionWork.EnterPublication();
        if (!original.Game.IsCurrent || !session.Owns(original.Owner, original.Context, false)) return Task.CompletedTask;
        if (!session.Owns(original.Owner, original.Context, true))
        {
            state.Message = Text("StatusBaseSnapshotChanged");
            recovery.Show(LocalMutationKind.Bank, state.Message, static () => { }, canRetry: false);
            return Task.CompletedTask;
        }
        var admitted = Begin(LocalMutationKind.Bank);
        if (admitted is null) return Task.CompletedTask;
        var attempt = original with { Admission = admitted.Admission };
        try { return PrepareAndSubmit(attempt, command, pending, package, payload); }
        catch (Exception ex)
        {
            transactions.TryApply(attempt, () =>
            {
                state.Message = ExceptionText(ex);
                recovery.Show(LocalMutationKind.Bank, state.Message, static () => { }, canRetry: false);
                presentation.Diagnose(ex);
            });
            return Task.CompletedTask;
        }
        finally { attempt.Admission.Dispose(); }
    }

    private Task PrepareAndSubmit(ClientSnapshotTransaction.Attempt attempt, ClientBankCommand command,
        ModBankStatusResponseDto pending, ModSnapshotPackageMetadataDto? package, byte[]? payload)
    {
        state.Message = Text("StatusUploadingConfirmation");
        mutations.Begin(LocalMutationKind.Bank, state.Message);
        if (package is null || payload is null)
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId,
                out var build, out var failure);
            if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
            if (!built)
            {
                string message = Text("StatusConfirmationFailed", ("CODE", "BuildSnapshotFailed"), ("MESSAGE", failure?.Message));
                state.Message = message;
                recovery.Show(LocalMutationKind.Bank, message, () => _ = Retry(attempt, command, pending, null, null), canRetry: true);
                return Task.CompletedTask;
            }
            package = build.Package!;
            payload = build.Payload!;
        }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission)) { Reject("StatusBusy"); return Task.CompletedTask; }
        attempt = attempt with { Admission = upload };
        state.Begin(attempt);
        bool publishing = false;
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.SubmitAsync(attempt.Context, command, package, payload, attempt.Owner.Token), r => r.Result, failure =>
            {
                if (failure.Network?.Response?.Status is { } status) state.View = status;
                string failed = command.Operation switch { ClientBankOperation.Borrow => "StatusConfirmationFailed", ClientBankOperation.RepayDebt => "StatusDebtRepayFailed", _ => "StatusRepayFailed" };
                string rejected = command.Operation switch { ClientBankOperation.Borrow => "StatusCreateRejected", ClientBankOperation.RepayDebt => "StatusDebtRepayRejected", _ => "StatusRepayRejected" };
                state.Message = FailureText(failure, failed, rejected);
                recovery.Show(LocalMutationKind.Bank, state.Message, () => _ = Retry(attempt, command, pending, package, payload),
                    canRetry: !publishing && failure.CanRetry);
            }, response =>
            {
                publishing = true;
                transactions.Publish(attempt, response.SnapshotId, response.LineageToken);
                transactions.TryApply(attempt, () =>
                {
                    var confirmed = response.Status ?? pending;
                    if (command.Operation == ClientBankOperation.Borrow)
                    {
                        var loan = response.Loan ?? command.Loan!;
                        if (response.Status is null) confirmed = ClientBankView.WithLoan(pending, loan);
                        runtime.ConfirmLoan(loan, confirmed);
                    }
                    transactions.TryApply(attempt, () =>
                    {
                        state.View = confirmed;
                        state.Message = command.Operation == ClientBankOperation.Borrow
                            ? Text("StatusCreated", ("PRINCIPAL", (response.Loan ?? command.Loan!).PrincipalSilver), ("TOTAL", (response.Loan ?? command.Loan!).TotalDueSilver))
                            : Text(command.Operation == ClientBankOperation.RepayDebt ? "StatusDebtRepaid" : "StatusRepaid", ("TOTAL", command.Silver));
                        presentation.Message(state.Message, rejected: false);
                        transactions.TryApply(attempt, () => { mutations.Reset(); recovery.Close(); }, requireBaseSnapshot: false);
                    }, requireBaseSnapshot: false);
                }, requireBaseSnapshot: false);
                if (transactions.TryApply(attempt, static () => { }, requireBaseSnapshot: false))
                {
                    attempt.Admission.Dispose();
                    Completed?.Invoke();
                }
            }));
    }

    private ClientSnapshotTransaction.Attempt? Begin(LocalMutationKind? continuation = null)
    {
        var context = session.ReadContext();
        var admission = mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId), continuation);
        if (admission != LocalMutationAdmission.Allowed)
        { RejectMessage(presentation.Admission(admission, mutations.Current)); return null; }
        if (state.Busy || !synchronization.TryBeginManual(out var lease)) { Reject("StatusBusy"); return null; }
        try
        {
            var attempt = transactions.Capture(lease);
            state.Begin(attempt);
            return attempt;
        }
        catch { lease.Dispose(); throw; }
    }

    private string FailureText<T>(ClientSnapshotTransactionFailure<T> failure, string failed, string rejected) =>
        failure.Exception is { } ex ? ExceptionText(ex) : Text(failure.Rejection is null ? failed : rejected,
            ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode),
            ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message));
    private string ExceptionText(Exception ex) => Text("StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text(key, args);
    private void Reject(string key) => RejectMessage(Text(key));
    private void RejectMessage(string message) { state.Message = message; presentation.Message(message, rejected: true); }
}
