using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed class ClientRaidLaunching
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientRaidPreparation preparation;
    private readonly IClientRaidLaunchTransport transport;
    private readonly IClientRaidLaunchPresentation presentation;
    private readonly Func<bool> pvpEnabled;

    internal ClientRaidLaunching(ClientSessionLifecycle session, ClientSnapshotMutationAdmission admission,
        ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations,
        ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery, ClientRaidPreparation preparation,
        IClientRaidLaunchTransport transport, IClientRaidLaunchPresentation presentation, Func<bool> pvpEnabled)
    { this.session = session; this.admission = admission; this.synchronization = synchronization; this.mutations = mutations;
        this.transactions = transactions; this.recovery = recovery; this.preparation = preparation;
        this.transport = transport; this.presentation = presentation; this.pvpEnabled = pvpEnabled; }

    internal event Action? Completed;

    internal Task StartAsync(IClientRaidLaunchRuntime runtime, ModWorldMapMarkerDto target)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!pvpEnabled()) { Reject(Text("PvpDisabled")); return Task.CompletedTask; }
        if (string.IsNullOrWhiteSpace(target.OwnerUserId) || string.IsNullOrWhiteSpace(target.OwnerColonyId)
            || string.IsNullOrWhiteSpace(target.WorldObjectId) || string.IsNullOrWhiteSpace(target.MapId))
        { Reject(Text("StatusTargetIncomplete")); return Task.CompletedTask; }
        var result = admission.Begin();
        if (result.Attempt is not { } attempt) { Reject(AdmissionText(result)); return Task.CompletedTask; }
        try
        {
            target = ClientProtocolJson.Deserialize<ModWorldMapMarkerDto>(ClientProtocolJson.Serialize(target), type => type);
            bool captured = runtime.TryCapture(attempt.Context, target, out var force, out var failure);
            if (!transactions.TryApply(attempt, static () => { })) { attempt.Admission.Dispose(); return Task.CompletedTask; }
            if (!captured) { Reject(failure); attempt.Admission.Dispose(); return Task.CompletedTask; }
            var command = new ClientRaidPreparationCommand(
                $"raid-start:{attempt.Context.UserId}:{attempt.Context.CurrentSnapshotId}:{force!.SourceLoadId}:{target.OwnerUserId}:{target.OwnerColonyId}:{DateTime.UtcNow.Ticks}",
                new(target.OwnerUserId, target.OwnerColonyId!, target.WorldObjectId!, target.MapId!, target.Tile));
            var job = new Job(runtime, target, force, command);
            presentation.Progress(Text("StatusBattleDownloading", ("TARGET", presentation.TargetLabel(target))));
            return RunOwnedAsync(new(attempt), job);
        }
        catch (Exception ex)
        {
            transactions.TryApply(attempt, () => { presentation.CloseProgress(); Reject(ExceptionText(ex)); presentation.Diagnose(ex); });
            attempt.Admission.Dispose(); return Task.CompletedTask;
        }
    }

    private async Task RunOwnedAsync(Execution execution, Job job)
    {
        try { await ClientSessionWork.Run(() => RunAsync(execution, job)).ConfigureAwait(false); }
        finally { execution.Attempt.Admission.Dispose(); }
    }

    private async Task RunAsync(Execution execution, Job job)
    {
        try
        {
            var attempt = execution.Attempt;
            var prepared = await preparation.PrepareAsync(attempt.Context, job.Preparation, attempt.Owner.Token).ConfigureAwait(false);
            Task opening = Task.CompletedTask, submission = Task.CompletedTask;
            await Publish(execution, () =>
            {
                if (prepared.Prepared is not { } accepted)
                { presentation.CloseProgress(); Reject(presentation.PreparationFailure(prepared.Failure!)); return; }
                job.Command = new(job.Preparation.IdempotencyKey, accepted.EventId, accepted.PreparationId,
                    accepted.Snapshot.SnapshotId, accepted.Guards?.ContractId ?? string.Empty, job.Preparation.Target, job.Force);
                presentation.Progress(presentation.Text("ClashOfRim.RemoteSessionMap.StatusGenerating", ("TARGET", presentation.TargetLabel(job.Target))));
                presentation.CloseProgress();
                opening = ClientGamePublication.EnqueueAsync(job.Runtime.EnqueueOpen, attempt.Owner.Token,
                    action => transactions.TryApply(execution.Attempt, action), () =>
                    {
                        if (job.Runtime.EntryFailure() is { } failure) { Reject(failure); return; }
                        mutations.Begin(LocalMutationKind.Raid, Text("StatusPrepareSnapshot"));
                        job.MutationStarted = true;
                        string? rejection = job.Runtime.Open(attempt.Context, job.Target, accepted);
                        if (!transactions.TryApply(execution.Attempt, static () => { })) return;
                        if (rejection is not null) { Fail(execution.Attempt, job, rejection, false); return; }
                        job.Opened = true;
                        submission = PrepareAndSubmit(execution, job);
                    });
            }).ConfigureAwait(false);
            await opening.ConfigureAwait(false);
            await submission.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ClientSessionWork.IsCurrent) { }
        catch (Exception ex) { await Publish(execution, () => Failed(execution.Attempt, job, ex)).ConfigureAwait(false); }
    }

    // Called only by the owning game thread, including explicit user retries.
    private Task PrepareAndSubmit(Execution execution, Job job)
    {
        var attempt = execution.Attempt;
        if (!job.Runtime.HasBattle)
        { Fail(attempt, job, Text("StatusMissingPreparedBattleMap", ("EVENTID", job.Command!.EventId)), false); return Task.CompletedTask; }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { Fail(attempt, job, presentation.Text("ClashOfRim.SnapshotUpload.Busy"), true); return Task.CompletedTask; }
        execution.Attempt = attempt = attempt with { Admission = upload };
        mutations.UpdateStatus(Text("StatusPrepareSnapshot")); presentation.Packaging();
        if (job.Snapshot is null)
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId, out var build, out var failure);
            if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
            if (!built)
            { Fail(attempt, job, Text("StatusAttackerSnapshotException", ("TYPE", "SaveToMemoryFailed"), ("MESSAGE", failure?.Message)), true); return Task.CompletedTask; }
            job.Snapshot = build.Package!; job.Payload = build.Payload!;
        }
        string status = Text("StatusSubmitting", ("TARGET", presentation.TargetLabel(job.Target)));
        mutations.UpdateStatus(status); presentation.Status(status);
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.SubmitAsync(attempt.Context, job.Command!, job.Snapshot!, job.Payload!, attempt.Owner.Token), r => r.Result,
            failure =>
            {
                // A partial publication may change our own base, but it cannot clear a different transaction.
                if (job.Publishing && session.ReadContext().CurrentSnapshotId != job.Snapshot!.SnapshotId
                    && session.ReadContext().CurrentSnapshotId != attempt.Context.CurrentSnapshotId) return;
                if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                string message = failure.Exception is { } ex ? ExceptionText(ex) : Text(
                    failure.Rejection is null ? "StatusCreateFailed" : "StatusCreateRejected",
                    ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode),
                    ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message));
                Fail(attempt, job, message, !job.Publishing && failure.CanRetry);
            }, response =>
            {
                if (response.Result?.Accepted != true || response.EventId != job.Command!.EventId
                    || response.AppliedSnapshotId != job.Snapshot!.SnapshotId)
                { Fail(attempt, job, Text("StatusConfirmationIncomplete"), false); return; }
                if (!job.Runtime.HasBattle)
                { Fail(attempt, job, Text("StatusMissingPreparedBattleMap", ("EVENTID", response.EventId)), false); return; }
                job.Publishing = true;
                transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                transactions.TryApply(attempt, () =>
                {
                    if (session.ReadContext().CurrentSnapshotId != response.AppliedSnapshotId) return;
                    job.Runtime.ApplyReceipt(response.AppliedSnapshotId!, ClientRaidServerDeadlines.Read(response));
                    presentation.Status(Text("StatusCreated", ("EVENTID", response.EventId)));
                    presentation.Message(Text("CreatedMessage", ("TARGET", presentation.TargetLabel(job.Target))), false);
                    mutations.Reset(); recovery.Close(); attempt.Admission.Dispose(); Completed?.Invoke();
                }, requireBaseSnapshot: false);
            }));
    }

    private Task Retry(ClientSnapshotTransaction.Attempt original, Job job)
    {
        using var p = ClientSessionWork.EnterPublication();
        var admitted = admission.Retry(original, LocalMutationKind.Raid);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged)
        { Fail(original, job, presentation.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged"), false); return Task.CompletedTask; }
        if (admitted.Attempt is not { } attempt)
        { Fail(original, job, AdmissionText(admitted), admitted.Failure == ClientSnapshotAdmissionFailure.Busy); return Task.CompletedTask; }
        var execution = new Execution(attempt);
        try { return FinishRetryAsync(execution, PrepareAndSubmit(execution, job)); }
        catch (Exception ex)
        {
            transactions.TryApply(execution.Attempt, () => Failed(execution.Attempt, job, ex));
            execution.Attempt.Admission.Dispose(); return Task.CompletedTask;
        }
    }

    private static async Task FinishRetryAsync(Execution execution, Task work)
    {
        try { await work.ConfigureAwait(false); }
        finally { execution.Attempt.Admission.Dispose(); }
    }

    private void Failed(ClientSnapshotTransaction.Attempt attempt, Job job, Exception ex)
    {
        presentation.CloseProgress(); presentation.Diagnose(ex);
        string message = ExceptionText(ex);
        if (job.MutationStarted) Fail(attempt, job, message, job.Opened && !job.Publishing);
        else Reject(message);
    }
    private void Fail(ClientSnapshotTransaction.Attempt attempt, Job job, string message, bool retry)
    { presentation.Status(message); recovery.Show(LocalMutationKind.Raid, message, () => _ = Retry(attempt, job), canRetry: retry); }
    private Task Publish(Execution execution, Action action) => ClientGamePublication.EnqueueAsync(execution.Attempt.Game.Enqueue,
        execution.Attempt.Owner.Token, run => transactions.TryApply(execution.Attempt, run), action);
    private void Reject(string message) { presentation.Status(message); presentation.Message(message, true); }
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text("ClashOfRim.Raid." + key, args);
    private string ExceptionText(Exception ex) => Text("StatusCreateException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private string AdmissionText(ClientSnapshotAdmissionResult result) => result.Failure == ClientSnapshotAdmissionFailure.Busy
        ? presentation.Text("ClashOfRim.SnapshotUpload.Busy") : presentation.Admission(result.Admission, mutations.Current);
    private sealed class Execution
    {
        internal Execution(ClientSnapshotTransaction.Attempt attempt) => Attempt = attempt;
        internal ClientSnapshotTransaction.Attempt Attempt;
    }
    private sealed class Job
    {
        internal Job(IClientRaidLaunchRuntime runtime, ModWorldMapMarkerDto target, ClientRaidForceManifest force, ClientRaidPreparationCommand preparation)
        { Runtime = runtime; Target = target; Force = force; Preparation = preparation; }
        internal readonly IClientRaidLaunchRuntime Runtime;
        internal readonly ModWorldMapMarkerDto Target;
        internal readonly ClientRaidForceManifest Force;
        internal readonly ClientRaidPreparationCommand Preparation;
        internal ClientRaidLaunchCommand? Command;
        internal bool MutationStarted, Opened, Publishing;
        internal ModSnapshotPackageMetadataDto? Snapshot;
        internal byte[]? Payload;
    }
}
