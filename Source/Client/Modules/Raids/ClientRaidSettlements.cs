using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed class ClientRaidSettlements : IClientRaidSettlementActions
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientRaidSupportLosses support;
    private readonly IClientEventConfirmationProtocol protocol;
    private readonly ClientEventInbox inbox;
    private readonly ClientLocalSnapshotConfirmation cleanup;
    private readonly IClientRaidSettlementPresentation presentation;
    internal ClientRaidSettlements(ClientSessionLifecycle session, ClientSnapshotMutationAdmission admission,
        ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions,
        ClientSnapshotFailureRecovery recovery, ClientRaidSupportLosses support, IClientEventConfirmationProtocol protocol,
        ClientEventInbox inbox, ClientLocalSnapshotConfirmation cleanup, IClientRaidSettlementPresentation presentation)
    {
        this.session = session; this.admission = admission; this.synchronization = synchronization; this.mutations = mutations;
        this.transactions = transactions; this.recovery = recovery; this.support = support; this.protocol = protocol;
        this.inbox = inbox; this.cleanup = cleanup; this.presentation = presentation;
    }
    internal event Action? Completed;
    // Composition hands expiry to the authoritative reload workflow.
    internal event Action<string>? Expired;

    internal FinishCommand? PrepareFinish(IClientRaidLifecycleBattle battle, string reason)
    {
        using var p = ClientSessionWork.EnterPublication();
        var command = new FinishCommand(this, battle, reason ?? "", session.CaptureOwnership(), session.ReadContext());
        return command.IsCurrent ? command : null;
    }
    internal sealed class FinishCommand
    {
        private readonly ClientRaidSettlements owner;
        private readonly IClientRaidLifecycleBattle battle;
        private readonly ClientRaidSettlementIdentity identity;
        private readonly string reason;
        private readonly ClientSessionLifecycle.Ownership sessionOwner;
        private readonly ClashOfRimClientNetworkContext context;
        private bool consumed;
        internal FinishCommand(ClientRaidSettlements owner, IClientRaidLifecycleBattle battle, string reason,
            ClientSessionLifecycle.Ownership sessionOwner, ClashOfRimClientNetworkContext context)
        { this.owner = owner; this.battle = battle; identity = battle.Identity; this.reason = reason; this.sessionOwner = sessionOwner; this.context = context; }
        internal bool IsCurrent => !consumed && !owner.session.IsUnavailable && owner.session.Owns(sessionOwner, context, requireSnapshot: false)
            && !string.IsNullOrWhiteSpace(identity.EventId) && identity == battle.Identity && owner.OwnsBattle(battle) && !battle.FinishInProgress;
        internal Task StartAsync()
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!IsCurrent) return Task.CompletedTask;
            consumed = true;
            return owner.StartAsync(battle, reason);
        }
    }

    public Task StartAsync(IClientRaidSettlementRuntime runtime, string reason)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (string.IsNullOrWhiteSpace(runtime.Identity.EventId) || !OwnsBattle(runtime) || !runtime.TryBegin()) return Task.CompletedTask;
        if (runtime.FinalRemaining <= TimeSpan.Zero) { Expire(runtime); return Task.CompletedTask; }
        var admitted = admission.Begin();
        if (admitted.Attempt is not { } attempt)
        { runtime.ResetFinish(); Reject(AdmissionText(admitted)); return Task.CompletedTask; }
        mutations.Begin(LocalMutationKind.Raid, Text("StatusSettlementUploading"));
        return RunOwnedAsync(new(attempt), new(runtime, reason ?? ""));
    }
    public void Expire(IClientRaidSettlementRuntime runtime, bool closeLocalMap = true)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!OwnsBattle(runtime) || string.IsNullOrWhiteSpace(runtime.Identity.EventId)) return;
        var owner = session.CaptureOwnership(); var context = session.ReadContext();
        if (!runtime.CloseExpired(closeLocalMap) || !session.Owns(owner, context, requireSnapshot: true)) return;
        recovery.Close();
        string message = Text("StatusSettlementFinalDeadlineExpired"); presentation.Status(message); presentation.Message(message, false);
        Expired?.Invoke(runtime.Identity.EventId);
    }
    private async Task RunOwnedAsync(Execution execution, Job job)
    {
        try
        {
            Task<ClientRaidSupportLossResult>? losses = null;
            // StartAsync and Retry enter on the owning game thread.
            transactions.TryApply(execution.Attempt, () =>
            {
                if (!job.Runtime.IsCurrent) return;
                job.Runtime.KeepFinish();
                if (job.SupportDone) return;
                job.Batch = job.Batch?.WithAdmission(execution.Attempt.Admission) ?? support.Capture(execution.Attempt, job.Runtime.SupportLosses);
                if (job.Batch is null) return;
                if (job.Batch.Count > 0) presentation.Status(Text("StatusSettlementReportingSupportLoss", ("COUNT", job.Batch.Count)));
                losses = support.ResolveAsync(job.Batch);
            });
            var result = losses is null ? null : await losses.ConfigureAwait(false);
            Task saving = Task.CompletedTask, submission = Task.CompletedTask;
            await Publish(execution, () =>
            {
                if (!job.Runtime.IsCurrent) return;
                if (result?.Failure is { } failure)
                { Fail(execution.Attempt, job, presentation.SupportFailure(failure), failure.CanRetry); return; }
                if (!job.SupportDone && result?.Success != true) return;
                job.SupportDone = true;
                saving = ClientGamePublication.EnqueueAsync(job.Runtime.EnqueueSaving, execution.Attempt.Owner.Token,
                    action => transactions.TryApply(execution.Attempt, action), () => submission = PrepareAndSubmit(execution, job));
            }).ConfigureAwait(false);
            await saving.ConfigureAwait(false);
            await submission.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execution.Attempt.Owner.Token.IsCancellationRequested) { }
        catch (Exception ex)
        { await Publish(execution, () => { presentation.Diagnose(ex); Fail(execution.Attempt, job, ExceptionText(ex), !job.Publishing && job.SupportDone); }).ConfigureAwait(false); }
        finally { execution.Attempt.Admission.Dispose(); }
    }
    private Task PrepareAndSubmit(Execution execution, Job job)
    {
        var attempt = execution.Attempt;
        if (!job.Runtime.IsCurrent) return Task.CompletedTask;
        if (job.Runtime.FinalRemaining <= TimeSpan.Zero) { attempt.Admission.Dispose(); Expire(job.Runtime); return Task.CompletedTask; }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { Fail(attempt, job, presentation.Text("ClashOfRim.SnapshotUpload.Busy"), true); return Task.CompletedTask; }
        execution.Attempt = attempt = attempt with { Admission = upload };
        presentation.Packaging();
        if (job.Command is null)
        {
            var capture = ClientSnapshotPreparation.Capture(attempt.Game, false);
            if (!transactions.TryApply(attempt, static () => { }) || !job.Runtime.IsCurrent) return Task.CompletedTask;
            ClientRaidSettlementSaveFailure? invalid = null;
            var bytes = capture.Failure is null ? ClientRaidSettlementSnapshot.Prepare(capture.SaveBytes ?? Array.Empty<byte>(),
                job.Identity.EventId, job.Identity.TargetMapId, out invalid) : null;
            string? reason = capture.Failure?.Message;
            if (capture.Failure is null && bytes is null)
            {
                reason = Text(invalid!.Key, ("MESSAGE", invalid.Message));
            }
            if (bytes is null) { Fail(attempt, job, Text("StatusSettlementSaveFailed", ("REASON", reason)), true); return Task.CompletedTask; }
            var build = ClientSnapshotPackageBuilder.FromSaveBytes(bytes, "raid-settlement", attempt.Context.UserId, attempt.Context.ColonyId, DateTime.UtcNow);
            if (!build.Success || build.Package is null || build.Payload is null)
            { Fail(attempt, job, Text("StatusSettlementSaveFailed", ("REASON", build.ErrorCode + " " + build.Message)), true); return Task.CompletedTask; }
            job.Command = new(ClientRaidSettlementIntent.Key(attempt.Context.UserId, job.Identity.EventId, job.Reason), job.Identity.EventId, null,
                attempt.Context.UserId, attempt.Context.ColonyId, attempt.Context.CurrentSnapshotId ?? "", build.Package, build.Payload, ClientRaidSettlementIntent.Result(job.Reason));
        }
        string status = Text("StatusSettlementUploading"); presentation.Status(status); mutations.UpdateStatus(status);
        return SubmitAsync(execution, job);
    }
    private async Task SubmitAsync(Execution execution, Job job)
    {
        var attempt = execution.Attempt;
        var command = job.Command! with { HttpTimeout = TimeSpan.FromSeconds(Math.Clamp(job.Runtime.FinalRemaining.TotalSeconds, 5, 60)) };
        Task cleaning = Task.CompletedTask;
        await ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => protocol.ConfirmAsync(attempt.Context, command, attempt.Owner.Token), r => r.Result,
            failure =>
            {
                // Failure callbacks can arrive on the worker; queue domain and UI effects.
                return ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                    action => transactions.TryApply(attempt, action, !job.Publishing), () =>
                    {
                        if (job.Publishing && session.ReadContext().CurrentSnapshotId != command.Snapshot.SnapshotId
                            && session.ReadContext().CurrentSnapshotId != attempt.Context.CurrentSnapshotId) return;
                        if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                        Fail(attempt, job, failure.Exception is { } ex ? ExceptionText(ex) : Text(
                            failure.Rejection is null ? "StatusSettlementFailed" : "StatusSettlementRejected",
                            ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode),
                            ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message),
                            ("VALIDATION", failure.Network?.Response?.ServerValidationResult)), !job.Publishing && failure.CanRetry);
                    });
            }, response =>
            {
                if (!job.Runtime.IsCurrent) return;
                if (response.Result?.Accepted != true || response.EventId != job.Identity.EventId || response.AppliedSnapshotId != command.Snapshot.SnapshotId)
                { Fail(attempt, job, Text("StatusConfirmationIncomplete"), false); return; }
                job.Publishing = true;
                transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                transactions.TryApply(attempt, () =>
                {
                    if (session.ReadContext().CurrentSnapshotId != response.AppliedSnapshotId || !job.Runtime.IsCurrent) return;
                    job.Closing = true;
                    if (!job.Runtime.CloseAccepted(job.Reason)
                        || !transactions.TryApply(attempt, static () => { }, requireBaseSnapshot: false)
                        || session.ReadContext().CurrentSnapshotId != response.AppliedSnapshotId) return;
                    inbox.ForgetGroup(job.Identity.EventId);
                    presentation.Status(Text("StatusSettlementSucceeded", ("SNAPSHOT", response.AppliedSnapshotId)));
                    presentation.Message(Text("SettlementSucceededMessage"), true);
                    // Keep local changes blocked across the evidence-to-cleanup handoff.
                    mutations.Begin(LocalMutationKind.RaidAttacker, Text("StatusAttackerSnapshotUploading"));
                    recovery.Close(); attempt.Admission.Dispose();
                    // New admission prevents the evidence finalizer from releasing cleanup.
                    var original = attempt.Context;
                    var confirmed = new ClashOfRimClientNetworkContext(original.ServerBaseUrl, original.UserId, original.ColonyId,
                        response.AppliedSnapshotId, original.SteamAuthTicket, original.OfflinePassword, original.AuthToken);
                    cleaning = StartCleanup(attempt with { Context = confirmed });
                }, requireBaseSnapshot: false);
            })).ConfigureAwait(false);
        await cleaning.ConfigureAwait(false);
    }
    private Task StartCleanup(ClientSnapshotTransaction.Attempt original)
    {
        var next = admission.Retry(original, LocalMutationKind.RaidAttacker);
        if (next.Attempt is { } continuation) return cleanup.ContinueAsync(CleanupRequest(), continuation);
        if (next.Failure == ClientSnapshotAdmissionFailure.Obsolete || mutations.Current?.Kind != LocalMutationKind.RaidAttacker) return Task.CompletedTask;
        string message = next.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged
            ? presentation.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged") : AdmissionText(next);
        presentation.Status(message);
        recovery.Show(LocalMutationKind.RaidAttacker, message, () => _ = StartCleanup(original), next.Failure == ClientSnapshotAdmissionFailure.Busy);
        return Task.CompletedTask;
    }
    private bool OwnsBattle(IClientRaidSettlementRuntime runtime)
    {
        var context = session.ReadContext();
        return runtime.IsCurrent && runtime.Identity.AttackerUserId == context.UserId && runtime.Identity.AttackerColonyId == context.ColonyId;
    }
    private ClientLocalSnapshotRequest CleanupRequest() => new()
    {
        Operation = LocalMutationKind.RaidAttacker, UploadingStatus = Text("StatusAttackerSnapshotUploading"), RemoveRaidBattleSessions = true, BestEffort = true,
        SetStatus = presentation.Status,
        BuildFailureStatus = result => Text("StatusAttackerSnapshotFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message)),
        BuildSuccessStatus = result => Text("StatusAttackerSnapshotSucceeded", ("SNAPSHOT", result.AcceptedSnapshotId)),
        BuildExceptionStatus = ex => Text("StatusAttackerSnapshotException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)),
        OnSuccessOnMainThread = _ => { presentation.Message(Text("AttackerSnapshotSucceededMessage"), true); Completed?.Invoke(); }
    };
    private Task Retry(ClientSnapshotTransaction.Attempt original, Job job)
    {
        using var p = ClientSessionWork.EnterPublication();
        var admitted = admission.Retry(original, LocalMutationKind.Raid);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Attempt is { } attempt) return RunOwnedAsync(new(attempt), job);
        Fail(original, job, admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged
            ? presentation.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged") : AdmissionText(admitted), admitted.Failure == ClientSnapshotAdmissionFailure.Busy);
        return Task.CompletedTask;
    }
    private void Fail(ClientSnapshotTransaction.Attempt attempt, Job job, string message, bool retry)
    {
        if (!job.Runtime.IsCurrent && !job.Closing) return;
        if (job.Runtime.IsCurrent && job.Runtime.FinalRemaining <= TimeSpan.Zero) { attempt.Admission.Dispose(); Expire(job.Runtime); return; }
        job.Runtime.ResetFinish(); presentation.Status(message);
        recovery.Show(LocalMutationKind.Raid, message, () => _ = Retry(attempt, job), retry);
    }
    private Task Publish(Execution execution, Action action) => ClientGamePublication.EnqueueAsync(execution.Attempt.Game.Enqueue,
        execution.Attempt.Owner.Token, run => transactions.TryApply(execution.Attempt, run), action);
    private void Reject(string message) { presentation.Status(message); presentation.Message(message, null); }
    private string Text(string key, params (string Name, object? Value)[] args) => presentation.Text("ClashOfRim.Raid." + key, args);
    private string ExceptionText(Exception ex) => Text("StatusSettlementException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private string AdmissionText(ClientSnapshotAdmissionResult result) => result.Failure == ClientSnapshotAdmissionFailure.Busy
        ? presentation.Text("ClashOfRim.SnapshotUpload.Busy") : presentation.Admission(result.Admission, mutations.Current);
    private sealed class Execution
    {
        internal Execution(ClientSnapshotTransaction.Attempt attempt) => Attempt = attempt;
        internal ClientSnapshotTransaction.Attempt Attempt;
    }
    private sealed class Job
    {
        internal Job(IClientRaidSettlementRuntime runtime, string reason) { Runtime = runtime; Identity = runtime.Identity; Reason = reason; }
        internal readonly IClientRaidSettlementRuntime Runtime;
        internal readonly ClientRaidSettlementIdentity Identity;
        internal readonly string Reason;
        internal ClientRaidSupportLosses.Batch? Batch;
        internal ClientEventConfirmationCommand? Command;
        internal bool SupportDone, Publishing, Closing;
    }
}
