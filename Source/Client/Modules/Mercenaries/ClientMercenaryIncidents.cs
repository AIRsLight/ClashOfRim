using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

// Pending incident debts become authoritative only in the following snapshot.
// Serialize that whole lifecycle so a later debt cannot miss an earlier capture.
internal sealed class ClientMercenaryIncidents
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientMercenaryState state;
    private readonly ClientBankState bank;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientMercenaryConfirmation confirmation;
    private readonly IClientMercenaryIncidentTransport transport;
    private readonly IClientMercenaryIncidentRuntime runtime;
    private readonly IClientMercenaryIncidentPresentation presentation;
    private ClientGameRequestTracker.Scope? queueScope;
    private CancellationTokenSource? queueLifetime;
    private Task tail = Task.CompletedTask;
    internal ClientMercenaryIncidents(ClientSessionLifecycle session, ClientMercenaryState state, ClientBankState bank,
        ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations, ClientSnapshotMutationAdmission admission,
        ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery, ClientMercenaryConfirmation confirmation,
        IClientMercenaryIncidentTransport transport, IClientMercenaryIncidentRuntime runtime, IClientMercenaryIncidentPresentation presentation)
    {
        this.session = session; this.state = state; this.bank = bank; this.synchronization = synchronization; this.mutations = mutations;
        this.admission = admission; this.transactions = transactions; this.recovery = recovery; this.confirmation = confirmation;
        this.transport = transport; this.runtime = runtime; this.presentation = presentation;
        session.IdentityChanged += _ => Reset();
        recovery.ReturningToMenu += Reset;
    }
    internal event Action? BankRefreshRequested;
    internal event Action? Completed;
    internal void Reset()
    {
        using var p = ClientSessionWork.EnterPublication();
        queueLifetime?.Cancel(); queueLifetime?.Dispose(); queueLifetime = null;
        queueScope = null; tail = Task.CompletedTask;
    }
    internal Task ReportAsync(string contractId, string kind, string? explicitKey = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (string.IsNullOrWhiteSpace(contractId) || string.IsNullOrWhiteSpace(kind)) return Task.CompletedTask;
        var context = session.ReadContext();
        if (!context.IsConfigured || string.IsNullOrWhiteSpace(context.CurrentSnapshotId))
        { state.Message = Text("StatusNotConfigured"); return Task.CompletedTask; }
        var scope = state.Requests.Capture();
        if (!ReferenceEquals(scope, queueScope))
        { Reset(); queueScope = scope; queueLifetime = CancellationTokenSource.CreateLinkedTokenSource(scope.Owner.Token); }
        string key = string.IsNullOrWhiteSpace(explicitKey)
            ? $"mercenary-incident:{context.UserId}:{context.ColonyId}:{contractId}:{kind}:{Guid.NewGuid():N}" : explicitKey!;
        var job = new Job(scope, new(key, contractId, kind, runtime.GameTicks), queueLifetime!.Token);
        Task previous = tail;
        tail = ClientSessionWork.Run(async () =>
        {
            try
            {
                await previous.WaitAsync(job.Token).ConfigureAwait(false);
                job.Token.ThrowIfCancellationRequested();
                await AttemptAsync(job, retry: false).ConfigureAwait(false);
                job.FirstAttempt.TrySetResult();
                // A failed report or confirmation stays at the head until recovery
                // succeeds or this queue's owner exits. Later reports cannot replace it.
                await job.Settled.Task.WaitAsync(job.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { }
            finally { job.FirstAttempt.TrySetResult(); }
        });
        return job.FirstAttempt.Task;
    }

    private async Task AttemptAsync(Job job, bool retry)
    {
        ClientSnapshotTransaction.Attempt? attempt = null;
        ClientGameRequestTracker.Request? request = null;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            if (retry)
            {
                using var p = ClientSessionWork.EnterPublication();
                if (!state.Requests.TryApply(job.Scope, static () => { })) return;
                var admitted = admission.Retry(job.Original!, LocalMutationKind.Mercenary);
                if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return;
                if (admitted.Attempt is not { } resumed)
                {
                    string message = admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged ? presentation.BaseSnapshotChanged
                        : admitted.Failure == ClientSnapshotAdmissionFailure.Busy ? Text("StatusBusy") : presentation.Admission(admitted.Admission, mutations.Current);
                    Fail(job, message, admitted.Failure == ClientSnapshotAdmissionFailure.Busy); return;
                }
                attempt = resumed;
                if (!synchronization.TryAcquireSnapshot(out var lease, attempt.Admission))
                { Fail(job, Text("StatusBusy"), true); return; }
                attempt = attempt with { Admission = lease };
            }
            else
            {
                while (attempt is null)
                {
                    await mutations.WaitUntilClearAsync(job.Token).ConfigureAwait(false);
                    var lease = await synchronization.AcquireSnapshotAsync(job.Token).ConfigureAwait(false);
                    using var p = ClientSessionWork.EnterPublication();
                    if (!state.Requests.TryApply(job.Scope, static () => { })) { lease.Dispose(); job.Settled.TrySetResult(); return; }
                    if (mutations.IsPending) { lease.Dispose(); continue; }
                    var context = session.ReadContext();
                    var check = mutations.CheckAdmission(context.IsConfigured, !string.IsNullOrWhiteSpace(context.CurrentSnapshotId));
                    if (check != LocalMutationAdmission.Allowed)
                    { lease.Dispose(); state.Message = presentation.Admission(check, mutations.Current); job.Settled.TrySetResult(); return; }
                    attempt = job.Original = transactions.Capture(lease);
                }
            }
            using (ClientSessionWork.EnterPublication())
            {
                if (!transactions.TryApply(attempt, static () => { })) return;
                request = state.Requests.Begin(attempt.Admission);
                if (request is null) { Fail(job, Text("StatusBusy"), true); return; }
                state.Message = Text("StatusReportingIncident"); mutations.Begin(LocalMutationKind.Mercenary, state.Message);
            }
            var result = await transport.ReportAsync(attempt.Context, job.Command, job.Token).ConfigureAwait(false);
            Task confirmed = Task.CompletedTask;
            await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, job.Token,
                action => Apply(attempt, request, action), () =>
                {
                    if (!result.Success || result.Response is null)
                    { Fail(job, Text("StatusIncidentFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message)), ClientNetworkFailurePolicy.CanRetry(result.ErrorCode, result.Message)); return; }
                    var response = result.Response;
                    if (response.BankStatus is not null) bank.View = response.BankStatus;
                    if (response.Result is { Accepted: false } rejection)
                    { Fail(job, Text("StatusIncidentRejected", ("CODE", rejection.ErrorCode), ("MESSAGE", rejection.Message)), false); return; }
                    if (response.Debt is { } debt && (string.IsNullOrWhiteSpace(debt.DebtId) || response.BankStatus is null))
                    { Fail(job, Text("StatusIncidentIncomplete"), false); return; }
                    job.Applying = true;
                    if (response.Debt is not null && response.BankStatus is not null)
                        runtime.ApplyDebt(ClientBankView.Copy(response.Debt), ClientBankView.Copy(response.BankStatus));
                    state.Message = response.Debt is null ? Text("StatusIncidentReportedNoDebt") : Text("StatusIncidentReported", ("FINE", response.Debt.AmountSilver));
                    if (job.Command.IsCompletion)
                    {
                        mutations.Reset(); recovery.Close(); state.Requests.End(request);
                        job.Settled.TrySetResult(); BankRefreshRequested?.Invoke(); Completed?.Invoke();
                    }
                    else
                    {
                        presentation.IncidentReported(state.Message);
                        confirmed = confirmation.ConfirmIncidentAsync(attempt, () =>
                        { state.Requests.End(request); job.Settled.TrySetResult(); });
                    }
                }).ConfigureAwait(false);
            await confirmed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            void Failure()
            {
                presentation.Diagnose(ex);
                string message = Text("StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
                if (job.Original is null) { state.Message = message; job.Settled.TrySetResult(); }
                else Fail(job, message, !job.Applying && ClientNetworkFailurePolicy.CanRetry(ex));
            }
            if (attempt is not null && request is not null) Apply(attempt, request, Failure);
            else state.Requests.TryApply(job.Scope, Failure);
        }
        finally { attempt?.Admission.Dispose(); if (request is not null) state.Requests.End(request); }
    }
    private bool Apply(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Action action)
    {
        bool applied = false;
        transactions.TryApply(attempt, () => applied = state.Requests.TryApply(request, action));
        return applied;
    }
    private void Fail(Job job, string message, bool retry)
    {
        state.Message = message;
        recovery.Show(LocalMutationKind.Mercenary, message, () => _ = ClientSessionWork.Run(() => AttemptAsync(job, retry: true)), retry);
    }
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text(key, args);
    private sealed class Job
    {
        internal Job(ClientGameRequestTracker.Scope scope, ClientMercenaryIncidentCommand command, CancellationToken token)
        { Scope = scope; Command = command; Token = token; }
        internal readonly ClientGameRequestTracker.Scope Scope;
        internal readonly ClientMercenaryIncidentCommand Command;
        internal readonly CancellationToken Token;
        internal readonly TaskCompletionSource FirstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously), Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ClientSnapshotTransaction.Attempt? Original;
        internal bool Applying;
    }
}
