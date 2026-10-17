using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

// Both kinds of hire own one quote, one local debit and one prepared payment.
// Arrival continuation retains its pawn; payment retries retain their bytes.
internal sealed class ClientMercenaryHiring
{
    private readonly ClientMercenaryState state;
    private readonly ClientBankState bank;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly IClientMercenaryRuntime runtime;
    private readonly IClientMercenaryHiringTransport transport;
    private readonly IClientMercenaryHiringPresentation presentation;
    internal ClientMercenaryHiring(ClientMercenaryState state, ClientBankState bank, ClientSnapshotMutationAdmission admission,
        ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions,
        ClientSnapshotFailureRecovery recovery, IClientMercenaryRuntime runtime, IClientMercenaryHiringTransport transport,
        IClientMercenaryHiringPresentation presentation)
    { this.state = state; this.bank = bank; this.admission = admission; this.synchronization = synchronization;
        this.mutations = mutations; this.transactions = transactions; this.recovery = recovery;
        this.runtime = runtime; this.transport = transport; this.presentation = presentation; }
    internal event Action? Completed;
    internal Action BindHire(string skill, int level, int days) => Bind(new(skill, level, days));
    internal Action BindGuard(string tier) => Bind(new("", 0, 0, tier));
    private Action Bind(ClientMercenarySelection selection)
    {
        var scope = state.Requests.Capture();
        return () => state.Requests.TryApply(scope, () => _ = StartAsync(selection));
    }
    internal Task StartAsync(ClientMercenarySelection selection)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (state.Busy) return Task.CompletedTask;
        var admitted = admission.Begin();
        if (admitted.Attempt is not { } attempt) { Reject(AdmissionText(admitted)); return Task.CompletedTask; }
        var request = state.Requests.Begin(attempt.Admission)!;
        bool handedOff = false;
        try
        {
            var source = runtime.Capture();
            if (source.Validate(0) is { } rejection) { Reject(rejection); return Task.CompletedTask; }
            state.Message = Text(selection.IsGuard ? "GuardStatusHiring" : "StatusHiring");
            handedOff = true;
            return QuoteAndPrepareAsync(attempt, request, selection, source);
        }
        catch (Exception ex) { Reject(ExceptionText(ex)); presentation.Diagnose(ex); return Task.CompletedTask; }
        finally { if (!handedOff) state.Requests.End(request); }
    }

    private async Task QuoteAndPrepareAsync(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request,
        ClientMercenarySelection selection, IClientMercenarySource source)
    {
        Job? job = null;
        Task submitted = Task.CompletedTask;
        try
        {
            await ClientSessionWork.Run(async () =>
            {
                if (!TryApply(attempt, request, static () => { })) return;
                var result = await transport.QuoteAsync(attempt.Context, selection, source.GameTicks, attempt.Owner.Token).ConfigureAwait(false);
                await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                    action => TryApply(attempt, request, action), () =>
                    {
                        if (!result.Success || result.Response is null)
                        { Reject(Text("StatusHireFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message))); return; }
                        var quote = result.Response;
                        if (quote.BankStatus is not null) bank.View = quote.BankStatus;
                        if (quote.Result is { Accepted: false } denied)
                        { Reject(Text("StatusHireRejected", ("CODE", denied.ErrorCode), ("MESSAGE", denied.Message))); return; }
                        if (quote.Price <= 0 || (selection.IsGuard && (string.IsNullOrWhiteSpace(quote.Tier)
                            || quote.PointRatio <= 0 || float.IsNaN(quote.PointRatio) || float.IsInfinity(quote.PointRatio))))
                        { Reject(Text("StatusMissingContract")); return; }
                        string prefix = selection.IsGuard ? "mercenary-guard" : "mercenary";
                        string key = $"{prefix}:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{attempt.Context.CurrentSnapshotId}:{Guid.NewGuid():N}";
                        var terms = new ClientMercenaryTerms(quote.Price, quote.SurgeryFine, quote.DeathFine, quote.Tier, quote.PointRatio);
                        job = new(source, new(key, prefix + "-" + Guid.NewGuid().ToString("N"), selection, terms,
                            source.GameTicks, checked(source.GameTicks + selection.Days * (long)source.TicksPerDay)));
                        if (source.Validate(quote.Price) is { } rejection) { Reject(rejection); return; }
                        mutations.Begin(LocalMutationKind.Mercenary, state.Message); job.Registered = true;
                        var local = source.Prepare(job.Command, () => job.MutationStarted = true);
                        submitted = AfterPreparation(attempt, request, job, local);
                    }).ConfigureAwait(false);
            });
            await submitted;
        }
        catch (Exception ex) { TryApply(attempt, request, () => LocalException(attempt, request, job, ex)); }
        finally { attempt.Admission.Dispose(); state.Requests.End(request); }
    }
    private Task AfterPreparation(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request,
        Job job, ClientMercenaryLocalResult result)
    {
        if (!TryApply(attempt, request, static () => { })) return Task.CompletedTask;
        job.Preparation = result.State;
        if (result.State != ClientMercenaryPreparation.Prepared)
        {
            Reject(result.Message);
            if (job.MutationStarted) Fail(attempt, request.Scope, job, result.Message, result.State == ClientMercenaryPreparation.AwaitingArrival);
            else if (job.Registered) mutations.Reset();
            return Task.CompletedTask;
        }
        return PrepareAndSubmit(attempt, request, job);
    }
    private Task PrepareAndSubmit(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job)
    {
        if (job.Package is null || job.Payload is null)
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId, out var build, out var failure);
            if (!TryApply(attempt, request, static () => { })) return Task.CompletedTask;
            if (!built)
            { Fail(attempt, request.Scope, job, Text("StatusConfirmationFailed", ("CODE", failure?.Code), ("MESSAGE", failure?.Message)), true); return Task.CompletedTask; }
            job.Package = build.Package!; job.Payload = build.Payload!;
        }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { Fail(attempt, request.Scope, job, Text("StatusBusy"), true); return Task.CompletedTask; }
        attempt = attempt with { Admission = upload };
        state.Requests.Track(request, upload);
        state.Message = Text("StatusUploadingConfirmation"); mutations.UpdateStatus(state.Message);
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.SubmitAsync(attempt.Context, job.Command, job.Package!, job.Payload!, attempt.Owner.Token), r => r.Result,
            failure => state.Requests.TryApply(request, () =>
            {
                if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                if (failure.Network?.Response?.BankStatus is { } status) bank.View = status;
                string message = failure.Exception is { } ex ? ExceptionText(ex)
                    : Text(failure.Rejection is null ? "StatusHireFailed" : "StatusHireRejected",
                        ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode), ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message));
                Fail(attempt, request.Scope, job, message, !job.Publishing && failure.CanRetry);
            }), response => state.Requests.TryApply(request, () =>
            {
                if (response.ContractId != job.Command.ContractId || string.IsNullOrWhiteSpace(response.AppliedSnapshotId)
                    || response.AppliedSnapshotId != job.Package!.SnapshotId)
                { Fail(attempt, request.Scope, job, Text("StatusConfirmationIncomplete"), false); return; }
                job.Publishing = true;
                transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                TryApply(attempt, request, () =>
                {
                    if (response.BankStatus is not null) bank.View = response.BankStatus;
                    state.Message = Text(job.Command.Selection.IsGuard ? "GuardStatusConfirmed" : "StatusHiredConfirmed", ("PRICE", job.Command.Terms.Price));
                    presentation.Message(state.Message, false);
                    mutations.Reset(); recovery.Close(); state.Requests.End(request);
                    Completed?.Invoke();
                }, requireBaseSnapshot: false);
            })));
    }
    private Task Retry(ClientSnapshotTransaction.Attempt original, ClientGameRequestTracker.Scope scope, Job job)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!state.Requests.TryApply(scope, static () => { })) return Task.CompletedTask;
        if (state.Busy) { Fail(original, scope, job, Text("StatusBusy"), true); return Task.CompletedTask; }
        var admitted = admission.Retry(original, LocalMutationKind.Mercenary);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged)
        { Fail(original, scope, job, presentation.BaseSnapshotChanged, false); return Task.CompletedTask; }
        if (admitted.Attempt is not { } attempt)
        { Fail(original, scope, job, AdmissionText(admitted), admitted.Failure == ClientSnapshotAdmissionFailure.Busy); return Task.CompletedTask; }
        return RetryAndFinishAsync(attempt, state.Requests.Begin(attempt.Admission)!, job);
    }
    private async Task RetryAndFinishAsync(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job)
    {
        try
        {
            if (job.Preparation == ClientMercenaryPreparation.AwaitingArrival)
            {
                // An exception during continuation is uncertain; do not leave the
                // previous AwaitingArrival marker eligible for another attempt.
                job.Preparation = ClientMercenaryPreparation.Failed;
                await AfterPreparation(attempt, request, job, job.Source.ResumeArrival());
            }
            else await PrepareAndSubmit(attempt, request, job);
        }
        catch (Exception ex) { TryApply(attempt, request, () => LocalException(attempt, request, job, ex)); }
        finally { attempt.Admission.Dispose(); state.Requests.End(request); }
    }
    private void LocalException(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job? job, Exception ex)
    {
        presentation.Diagnose(ex); string message = ExceptionText(ex); Reject(message);
        if (job?.MutationStarted == true) Fail(attempt, request.Scope, job, message, !job.Publishing && job.Preparation == ClientMercenaryPreparation.Prepared);
        else if (job?.Registered == true) mutations.Reset();
    }
    private void Fail(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Scope scope, Job job, string message, bool retry)
    { state.Message = message; recovery.Show(LocalMutationKind.Mercenary, message, () => _ = Retry(attempt, scope, job), canRetry: retry); }
    private bool TryApply(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Action action, bool requireBaseSnapshot = true)
    {
        bool applied = false;
        transactions.TryApply(attempt, () => applied = state.Requests.TryApply(request, action), requireBaseSnapshot);
        return applied;
    }
    private string AdmissionText(ClientSnapshotAdmissionResult result) => result.Failure == ClientSnapshotAdmissionFailure.Busy
        ? Text("StatusBusy") : presentation.Admission(result.Admission, mutations.Current);
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text(key, args);
    private string ExceptionText(Exception ex) => Text("StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private void Reject(string message) { state.Message = message; presentation.Message(message, true); }
    private sealed class Job
    {
        internal Job(IClientMercenarySource source, ClientMercenaryCommand command) { Source = source; Command = command; }
        internal readonly IClientMercenarySource Source;
        internal readonly ClientMercenaryCommand Command;
        internal bool Registered, MutationStarted, Publishing;
        internal ClientMercenaryPreparation Preparation = ClientMercenaryPreparation.Failed;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
    }
}
