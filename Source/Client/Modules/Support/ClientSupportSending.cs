using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Support;

internal sealed class ClientSupportSending
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientGameRequestTracker requests;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly IClientSupportSendingTransport transport;
    private readonly IClientSupportPresentation presentation;
    internal ClientSupportSending(ClientSessionLifecycle session, IClientSnapshotGame game, ClientSnapshotMutationAdmission admission,
        ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions,
        ClientSnapshotFailureRecovery recovery, IClientSupportSendingTransport transport, IClientSupportPresentation presentation)
    {
        this.session = session; requests = new(session, game, static () => { }); this.admission = admission;
        this.synchronization = synchronization; this.mutations = mutations; this.transactions = transactions;
        this.recovery = recovery; this.transport = transport; this.presentation = presentation;
    }
    internal event Action? Completed;
    internal ClientGameRequestTracker.Scope CaptureScope() => requests.Capture();
    internal bool InScope(ClientGameRequestTracker.Scope scope, Action action) => requests.TryApply(scope, action);

    internal Task SendAsync(IClientSupportSource source, ClientSupportTarget target, ClientSupportSelection selection)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (requests.Busy) { Reject(presentation.Busy); return Task.CompletedTask; }
        var admitted = admission.Begin();
        if (admitted.Attempt is not { } attempt) { Reject(AdmissionText(admitted)); return Task.CompletedTask; }
        return RunAsync(attempt, requests.Begin(attempt.Admission)!, new(source, target, selection));
    }

    private async Task RunAsync(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job)
    {
        try
        {
            if (!Apply(attempt, request, static () => { })) return;
            if (!job.Prepared)
            {
                if (string.IsNullOrWhiteSpace(job.Target.UserId) || string.IsNullOrWhiteSpace(job.Target.ColonyId)
                    || string.IsNullOrWhiteSpace(job.Target.MapId)) { Reject(Text("StatusNoTargets")); return; }
                var captured = job.Source.Capture(attempt.Context, job.Target);
                if (!Apply(attempt, request, static () => { })) return;
                if (captured.Rejection is not null || captured.Pawn is null)
                { Reject(captured.Rejection ?? Text("StatusNoPawns")); return; }
                // Freeze nested package data once; a retry must not observe UI or source mutations.
                var pawn = captured.Pawn with { Package = ClientProtocolJson.Deserialize<ModPawnExchangePackageDto>(
                    ClientProtocolJson.Serialize(captured.Pawn.Package), type => "Invalid support pawn package: " + type) };
                string key = $"support-pawn:{attempt.Context.UserId}:{attempt.Context.CurrentSnapshotId}:{pawn.GlobalKey}:{job.Target.UserId}:{job.Target.ColonyId}:{Guid.NewGuid():N}";
                job.Command = new(key, job.Target, job.Selection, pawn);
                mutations.Begin(LocalMutationKind.SupportSend, Text("StatusReserving")); job.Registered = true;
                string? rejection = job.Source.Remove(job.Command, () => job.MutationStarted = true);
                if (!Apply(attempt, request, static () => { })) return;
                if (rejection is not null)
                {
                    Reject(rejection);
                    if (job.MutationStarted) Fail(attempt, request.Scope, job, rejection, false);
                    else mutations.Reset();
                    return;
                }
                job.Prepared = true;
            }
            await PrepareAndSubmit(attempt, request, job);
        }
        catch (Exception ex)
        {
            Apply(attempt, request, () =>
            {
                string message = ExceptionText(ex); Reject(message); presentation.Diagnose(ex);
                if (job.MutationStarted) Fail(attempt, request.Scope, job, message, job.Prepared && !job.Publishing);
                else if (job.Registered) mutations.Reset();
            });
        }
        finally { attempt.Admission.Dispose(); requests.End(request); }
    }

    private Task PrepareAndSubmit(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Job job)
    {
        mutations.UpdateStatus(Text("StatusPackagingSnapshot"));
        if (job.Snapshot is null)
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId, out var build, out var failure);
            if (!Apply(attempt, request, static () => { })) return Task.CompletedTask;
            if (!built)
            { Fail(attempt, request.Scope, job, Text("StatusSnapshotBuildFailed", ("REASON", failure?.Message)), true); return Task.CompletedTask; }
            job.Snapshot = build.Package!; job.Payload = build.Payload!;
        }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { Fail(attempt, request.Scope, job, presentation.Busy, true); return Task.CompletedTask; }
        attempt = attempt with { Admission = upload }; requests.Track(request, upload);
        presentation.Status(Text("StatusSubmitting", ("PAWN", job.Command!.Pawn.Name), ("TARGET", job.Target.Label)));
        mutations.UpdateStatus(Text("StatusSubmittingTransaction"));
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.SendAsync(attempt.Context, job.Command!, job.Snapshot!, job.Payload!, attempt.Owner.Token), r => r.Result,
            failure => requests.TryApply(request, () =>
            {
                string? current = session.ReadContext().CurrentSnapshotId;
                if (job.Publishing && current != job.Snapshot!.SnapshotId && current != attempt.Context.CurrentSnapshotId) return;
                if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                string message = failure.Exception is { } ex ? ExceptionText(ex) : Text(
                    failure.Rejection is null ? "StatusCreateFailed" : "StatusCreateRejected",
                    ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode),
                    ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message));
                Fail(attempt, request.Scope, job, message, !job.Publishing && failure.CanRetry);
            }), response => requests.TryApply(request, () =>
            {
                if (response.Result?.Accepted != true || string.IsNullOrWhiteSpace(response.EventId)
                    || response.AppliedSnapshotId != job.Snapshot!.SnapshotId)
                { Fail(attempt, request.Scope, job, Text("StatusConfirmationIncomplete"), false); return; }
                job.Publishing = true;
                transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                Apply(attempt, request, () =>
                {
                    if (session.ReadContext().CurrentSnapshotId != response.AppliedSnapshotId) return;
                    presentation.Status(Text("StatusCreated", ("PAWN", job.Command!.Pawn.Name), ("EVENTID", response.EventId)));
                    presentation.Message(Text("CreatedMessage", ("PAWN", job.Command.Pawn.Name)), false);
                    mutations.Reset(); recovery.Close(); requests.End(request);
                    Completed?.Invoke();
                }, requireBase: false);
            })));
    }

    private Task Retry(ClientSnapshotTransaction.Attempt original, ClientGameRequestTracker.Scope scope, Job job)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!requests.TryApply(scope, static () => { })) return Task.CompletedTask;
        if (requests.Busy) { Fail(original, scope, job, presentation.Busy, true); return Task.CompletedTask; }
        var admitted = admission.Retry(original, LocalMutationKind.SupportSend);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged)
        { Fail(original, scope, job, presentation.BaseSnapshotChanged, false); return Task.CompletedTask; }
        if (admitted.Attempt is not { } attempt)
        { Fail(original, scope, job, AdmissionText(admitted), admitted.Failure == ClientSnapshotAdmissionFailure.Busy); return Task.CompletedTask; }
        return RunAsync(attempt, requests.Begin(attempt.Admission)!, job);
    }
    private void Fail(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Scope scope, Job job, string message, bool retry)
    { presentation.Status(message); recovery.Show(LocalMutationKind.SupportSend, message, () => _ = Retry(attempt, scope, job), canRetry: retry); }
    private bool Apply(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Action action, bool requireBase = true)
    {
        bool applied = false;
        transactions.TryApply(attempt, () => applied = requests.TryApply(request, action), requireBase);
        return applied;
    }
    private string AdmissionText(ClientSnapshotAdmissionResult result) => result.Failure == ClientSnapshotAdmissionFailure.Busy
        ? presentation.Busy : presentation.Admission(result.Admission, mutations.Current);
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text(key, args);
    private string ExceptionText(Exception ex) => Text("StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private void Reject(string message) { presentation.Status(message); presentation.Message(message, true); }
    private sealed class Job
    {
        internal readonly IClientSupportSource Source;
        internal readonly ClientSupportTarget Target;
        internal readonly ClientSupportSelection Selection;
        internal Job(IClientSupportSource source, ClientSupportTarget target, ClientSupportSelection selection)
        { Source = source; Target = target; Selection = selection; }
        internal ClientSupportCommand? Command;
        internal bool Registered, MutationStarted, Prepared, Publishing;
        internal ModSnapshotPackageMetadataDto? Snapshot;
        internal byte[]? Payload;
    }
}
