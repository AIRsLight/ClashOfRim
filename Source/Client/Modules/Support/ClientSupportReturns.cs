using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Support;

// A return owns departure, durable preparation, snapshot confirmation and the
// final server report. Keep its queue head until recovery finishes that lifecycle.
internal sealed class ClientSupportReturns
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientGameRequestTracker requests;
    private readonly ClientGameWorkQueue queue;
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientLocalSnapshotConfirmation confirmation;
    private readonly IClientSupportReturnRuntime runtime;
    private readonly IClientSupportReturnTransport transport;
    private readonly IClientSupportPresentation presentation;
    internal ClientSupportReturns(ClientSessionLifecycle session, IClientSnapshotGame game, ClientSnapshotMutationAdmission admission,
        ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery,
        ClientLocalSnapshotConfirmation confirmation, IClientSupportReturnRuntime runtime, IClientSupportReturnTransport transport,
        IClientSupportPresentation presentation)
    {
        this.session = session; this.admission = admission; this.mutations = mutations; this.transactions = transactions;
        this.recovery = recovery; this.confirmation = confirmation; this.runtime = runtime; this.transport = transport; this.presentation = presentation;
        requests = new(session, game, jobs.Clear); queue = new(requests);
        session.IdentityChanged += _ => Reset(); recovery.ReturningToMenu += Reset;
    }
    internal event Action? Completed;
    internal void Reset() { queue.Reset(); requests.Reset(); }

    internal bool TryStartDeparture(string eventId, string reason)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (string.IsNullOrWhiteSpace(eventId)) return false;
        requests.Capture();
        var admitted = admission.Begin();
        if (admitted.Attempt is not { } attempt) return false;
        bool changed = false, ready;
        try
        {
            var source = runtime.Find(eventId);
            if (source?.View is not { Permanent: false, Departing: false, HasPawn: true }) return false;
            ready = source.Depart(reason, () => changed = true);
        }
        catch (Exception ex)
        {
            presentation.Diagnose(ex); presentation.Status(ExceptionText(ex));
            if (changed) recovery.Show(LocalMutationKind.Support, ExceptionText(ex), static () => { }, canRetry: false);
            return false;
        }
        finally { attempt.Admission.Dispose(); }
        if (ready) _ = ConfirmAsync(eventId, reason);
        return true;
    }

    // Called by the quest tick. Repeated ticks refer to the same pending job.
    internal Task? ConfirmAsync(string eventId, string reason)
    {
        using var p = ClientSessionWork.EnterPublication();
        var context = session.ReadContext();
        if (string.IsNullOrWhiteSpace(eventId) || !context.IsConfigured || string.IsNullOrWhiteSpace(context.CurrentSnapshotId)) return null;
        var scope = requests.Capture();
        if (jobs.TryGetValue(eventId, out var existing)) return existing.FirstAttempt.Task;
        if (runtime.Find(eventId)?.View is not { Permanent: false, Departing: true, CanFinish: true }) return null;
        var job = new Job(scope, eventId, reason); jobs.Add(eventId, job);
        _ = queue.Enqueue(scope, async token =>
        {
            job.Token = token;
            await PrepareAsync(job).ConfigureAwait(false);
            job.FirstAttempt.TrySetResult();
            await job.Settled.Task.WaitAsync(token).ConfigureAwait(false);
        }, () =>
        {
            job.FirstAttempt.TrySetResult();
            requests.TryApply(scope, () => { if (jobs.TryGetValue(eventId, out var current) && ReferenceEquals(current, job)) jobs.Remove(eventId); });
        });
        return job.FirstAttempt.Task;
    }

    private async Task PrepareAsync(Job job)
    {
        ClientSnapshotTransaction.Attempt? attempt = null;
        ClientGameRequestTracker.Request? request = null;
        try
        {
            var admitted = await admission.WaitAsync(job.Token).ConfigureAwait(false);
            using (ClientSessionWork.EnterPublication())
            {
                attempt = admitted.Attempt;
                if (!requests.TryApply(job.Scope, static () => { })) return;
                if (attempt is null)
                { presentation.Status(presentation.Admission(admitted.Admission, mutations.Current)); job.Settled.TrySetResult(); return; }
                request = requests.Begin(attempt.Admission)!;
            }
            Task confirmed = Task.CompletedTask, reported = Task.CompletedTask;
            await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, job.Token, action => Apply(attempt, request, action), () =>
            {
                job.Source = runtime.Find(job.EventId);
                if (job.Source?.View is not { Permanent: false, Departing: true, CanFinish: true })
                { job.Settled.TrySetResult(); return; }
                var payload = job.Source.Prepare(attempt.Context, job.Reason, () =>
                    mutations.Begin(LocalMutationKind.Support, Text("StatusReturnSnapshotUploading", ("PAWN", job.EventId))));
                // Freeze nested data from the engine adapter for notification retries.
                payload = payload with { Package = payload.Package is null ? null : ClientProtocolJson.Deserialize<ModPawnExchangePackageDto>(
                    ClientProtocolJson.Serialize(payload.Package), type => "Invalid support return package: " + type) };
                job.Command = new($"support-finish:{attempt.Context.UserId}:{attempt.Context.ColonyId}:{payload.EventId}:{payload.FinishReason}", payload);
                confirmed = confirmation.ContinueAsync(new ClientLocalSnapshotRequest
                {
                    Operation = LocalMutationKind.Support,
                    UploadingStatus = Text("StatusReturnSnapshotUploading", ("PAWN", payload.PawnName)),
                    SetStatus = presentation.Status,
                    BuildSuccessStatus = result => Text("StatusReturnSnapshotConfirmed", ("PAWN", payload.PawnName), ("SNAPSHOT", result.AcceptedSnapshotId)),
                    OnSuccessOnMainThread = _ =>
                    {
                        // The confirmation owns its own request; release ours before
                        // report admission so an old finally cannot close new work.
                        requests.End(request);
                        reported = ReportAsync(job, retry: false);
                    }
                }, attempt);
            }).ConfigureAwait(false);
            await confirmed.ConfigureAwait(false);
            await reported.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            void Failure() { presentation.Diagnose(ex); Fail(job, ExceptionText(ex), retry: false); }
            if (attempt is not null && request is not null) Apply(attempt, request, Failure);
            else requests.TryApply(job.Scope, Failure);
        }
        finally { attempt?.Admission.Dispose(); if (request is not null) requests.End(request); }
    }

    private Task ReportAsync(Job job, bool retry)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!requests.TryApply(job.Scope, static () => { })) return Task.CompletedTask;
        var admitted = retry ? admission.Retry(job.ReportAttempt!, LocalMutationKind.Support) : admission.Begin();
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Attempt is not { } attempt)
        {
            // Confirmation invokes this synchronously before releasing publication;
            // another business operation cannot enter between its completion and ours.
            string message = admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged ? presentation.BaseSnapshotChanged
                : admitted.Failure == ClientSnapshotAdmissionFailure.Busy ? presentation.Busy : presentation.Admission(admitted.Admission, mutations.Current);
            Fail(job, message, retry && admitted.Failure == ClientSnapshotAdmissionFailure.Busy); return Task.CompletedTask;
        }
        job.ReportAttempt ??= attempt;
        var request = requests.Begin(attempt.Admission)!;
        mutations.Begin(LocalMutationKind.Support, Text("StatusReturnSnapshotConfirmed", ("PAWN", job.Command!.Payload.PawnName), ("SNAPSHOT", attempt.Context.CurrentSnapshotId)));
        return ClientSessionWork.Run(async () =>
        {
            try
            {
                await transactions.ExecuteAsync(attempt, () => transport.FinishAsync(attempt.Context, job.Command!, job.Token), r => r.Result,
                    failure => requests.TryApply(request, () =>
                    {
                        if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                        string message = failure.Exception is { } ex ? ExceptionText(ex) : Text(
                            failure.Rejection is null ? "StatusFinishFailed" : "StatusFinishRejected",
                            ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode),
                            ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message));
                        Fail(job, message, !job.Completing && failure.CanRetry);
                    }), response => requests.TryApply(request, () =>
                    {
                        if (response.Result?.Accepted != true || string.IsNullOrWhiteSpace(response.ReturnEventId) && string.IsNullOrWhiteSpace(response.NotificationEventId))
                        { Fail(job, Text("StatusReturnConfirmationIncomplete"), retry: false); return; }
                        job.Completing = true; job.Source!.Complete();
                        presentation.Status(Text("StatusFinished", ("PAWN", job.Command!.Payload.PawnName), ("EVENTID", response.ReturnEventId ?? response.NotificationEventId)));
                        presentation.Message(Text("FinishedMessage", ("PAWN", job.Command.Payload.PawnName)), false);
                        mutations.Reset(); recovery.Close(); requests.End(request);
                        job.Settled.TrySetResult(); Completed?.Invoke();
                    })).ConfigureAwait(false);
            }
            finally { attempt.Admission.Dispose(); requests.End(request); }
        });
    }
    private bool Apply(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Action action)
    {
        bool applied = false;
        transactions.TryApply(attempt, () => applied = requests.TryApply(request, action));
        return applied;
    }
    private void Fail(Job job, string message, bool retry)
    { presentation.Status(message); recovery.Show(LocalMutationKind.Support, message, () => _ = ReportAsync(job, retry: true), retry); }
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text(key, args);
    private string ExceptionText(Exception ex) => Text("StatusFinishException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private sealed class Job
    {
        internal Job(ClientGameRequestTracker.Scope scope, string eventId, string reason) { Scope = scope; EventId = eventId; Reason = reason; }
        internal readonly ClientGameRequestTracker.Scope Scope;
        internal readonly string EventId, Reason;
        internal readonly TaskCompletionSource FirstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously), Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal IClientSupportReturnSource? Source;
        internal ClientSupportReturnCommand? Command;
        internal ClientSnapshotTransaction.Attempt? ReportAttempt;
        internal bool Completing;
    }
}
