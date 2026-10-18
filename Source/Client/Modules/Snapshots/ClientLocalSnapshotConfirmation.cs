using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal sealed record ClientLocalSnapshotRequest
{
    internal LocalMutationKind Operation { get; init; }
    internal string UploadingStatus { get; init; } = "";
    internal bool RemoveRaidBattleSessions { get; init; }
    internal bool BestEffort { get; init; }
    internal string? ConfirmationOperation { get; init; }
    internal string SnapshotUploadKind { get; init; } = ModSnapshotUploadKinds.EventApplicationConfirmation;
    internal Action<string>? SetStatus { get; init; }
    internal Func<ModSnapshotUploadResult, string>? BuildFailureStatus { get; init; }
    internal Func<ModSnapshotUploadResult, string>? BuildSuccessStatus { get; init; }
    internal Func<Exception, string>? BuildExceptionStatus { get; init; }
    internal Action<ModSnapshotUploadResult>? OnSuccessOnMainThread { get; init; }
}

internal interface IClientLocalSnapshotPresentation
{
    string Busy { get; }
    string BaseSnapshotChanged { get; }
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    ModSnapshotUploadResult Result(ClientSnapshotUploadResult result);
    string Failed(string status, string? code, string? reason);
    string Succeeded(string? snapshotId);
    string Exception(string status, Exception exception);
    void DefaultStatus(string message);
    void Message(string message, bool accepted, bool bestEffort);
    void Diagnose(LocalMutationKind operation, string message, Exception? exception);
}

// Confirms game changes that already happened (recall, support return, raid cleanup).
// It owns only upload/recovery; it never replays the domain's game mutations.
internal sealed class ClientLocalSnapshotConfirmation
{
    private readonly ClientGameRequestTracker requests;
    private readonly ClientSessionLifecycle session;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientSnapshotUploader uploader;
    private readonly IClientLocalSnapshotPresentation presentation;
    internal ClientLocalSnapshotConfirmation(ClientSessionLifecycle session, IClientSnapshotGame game,
        ClientSnapshotMutationAdmission admission, ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations,
        ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery, ClientSnapshotUploader uploader,
        IClientLocalSnapshotPresentation presentation)
    {
        requests = new(session, game, static () => { }); this.session = session; this.admission = admission; this.synchronization = synchronization;
        this.mutations = mutations; this.transactions = transactions; this.recovery = recovery; this.uploader = uploader; this.presentation = presentation;
    }
    internal event Action? Completed;
    internal Task StartAsync(ClientLocalSnapshotRequest intent) => Start(intent, null, null);
    internal Task ContinueAsync(ClientLocalSnapshotRequest intent, ClientSnapshotTransaction.Attempt attempt)
    {
        using var p = ClientSessionWork.EnterPublication();
        Task work = Task.CompletedTask;
        transactions.TryApply(attempt, () => work = StartAdmitted(intent, attempt));
        return work;
    }

    private Task Start(ClientLocalSnapshotRequest intent, ClientSnapshotTransaction.Attempt? original, ClientGameRequestTracker.Scope? scope)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (scope is not null && !requests.TryApply(scope, static () => { })) return Task.CompletedTask;
        requests.Capture(); // Release an obsolete request before testing global admission.
        var admitted = original is null ? admission.Begin(intent.Operation) : admission.Retry(original, intent.Operation);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Attempt is not { } attempt)
        {
            string message = admitted.Failure switch
            {
                ClientSnapshotAdmissionFailure.Busy => presentation.Busy,
                ClientSnapshotAdmissionFailure.BaseSnapshotChanged => presentation.BaseSnapshotChanged,
                _ => presentation.Admission(admitted.Admission, mutations.Current)
            };
            Status(intent, message);
            if (original is not null && scope is not null)
                Fail(intent, original, scope, message, admitted.Failure == ClientSnapshotAdmissionFailure.Busy);
            else presentation.Message(message, false, intent.BestEffort);
            return Task.CompletedTask;
        }
        return StartAdmitted(intent, attempt);
    }
    private Task StartAdmitted(ClientLocalSnapshotRequest intent, ClientSnapshotTransaction.Attempt attempt)
    {
        requests.Capture();
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { attempt.Admission.Dispose(); Status(intent, presentation.Busy); return Task.CompletedTask; }
        attempt = attempt with { Admission = upload };
        var request = requests.Begin(upload);
        if (request is null) { upload.Dispose(); Status(intent, presentation.Busy); return Task.CompletedTask; }
        mutations.Begin(intent.Operation, intent.UploadingStatus); Status(intent, intent.UploadingStatus);
        return ExecuteAsync(intent, attempt, request);
    }

    private async Task ExecuteAsync(ClientLocalSnapshotRequest intent, ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request)
    {
        string? publishingSnapshot = null;
        bool Apply(Action action, bool accepted = false)
        {
            bool applied = false;
            TryApply(attempt, request, () =>
            {
                string? current = session.ReadContext().CurrentSnapshotId;
                if (publishingSnapshot is not null && current != publishingSnapshot && (accepted || current != attempt.Context.CurrentSnapshotId)) return;
                action(); applied = true;
            }, requireBase: publishingSnapshot is null);
            return applied;
        }
        try
        {
            await ClientSessionWork.Run(async () =>
            {
                Task<ClientSnapshotUploadResult>? upload = null;
                TryApply(attempt, request, () => upload = uploader.UploadAsync(intent.RemoveRaidBattleSessions, intent.ConfirmationOperation,
                    intent.SnapshotUploadKind, null, attempt.Admission, attempt.Owner.Token,
                    new(() => requests.TryApply(request, static () => { }), id => publishingSnapshot = id)));
                if (upload is null) return;
                var result = await upload.ConfigureAwait(false);
                await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                    action => Apply(action, accepted: result.Failure is null), () =>
                    {
                        var exposed = presentation.Result(result);
                        if (!exposed.Success)
                        {
                            string failure = intent.BuildFailureStatus?.Invoke(exposed) ?? presentation.Failed(intent.UploadingStatus, exposed.ErrorCode, exposed.Message);
                            FailedResult(intent, attempt, request, failure, publishingSnapshot is null && ClientNetworkFailurePolicy.CanRetry(exposed.ErrorCode, exposed.Message));
                            return;
                        }
                        string success = intent.BuildSuccessStatus?.Invoke(exposed) ?? presentation.Succeeded(exposed.AcceptedSnapshotId);
                        Status(intent, success); presentation.Message(success, true, intent.BestEffort);
                        Complete(request);
                        try { intent.OnSuccessOnMainThread?.Invoke(exposed); }
                        catch (Exception ex) { presentation.Diagnose(intent.Operation, success, ex); }
                        finally { Completed?.Invoke(); }
                    }).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                action => Apply(action), () =>
                {
                    string message = intent.BuildExceptionStatus?.Invoke(ex) ?? presentation.Exception(intent.UploadingStatus, ex);
                    presentation.Diagnose(intent.Operation, message, ex);
                    FailedResult(intent, attempt, request, message, publishingSnapshot is null && ClientNetworkFailurePolicy.CanRetry(ex));
                }).ConfigureAwait(false);
        }
        finally { attempt.Admission.Dispose(); requests.End(request); }
    }
    private void FailedResult(ClientLocalSnapshotRequest intent, ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, string message, bool retry)
    {
        Status(intent, message);
        if (intent.BestEffort)
        {
            presentation.Diagnose(intent.Operation, message, null);
            presentation.Message(message, false, true);
            Complete(request);
            Completed?.Invoke();
        }
        else Fail(intent, attempt, request.Scope, message, retry);
    }
    private void Fail(ClientLocalSnapshotRequest intent, ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Scope scope, string message, bool retry)
    { recovery.Show(intent.Operation, message, () => _ = Start(intent, attempt, scope), retry); }
    private bool TryApply(ClientSnapshotTransaction.Attempt attempt, ClientGameRequestTracker.Request request, Action action, bool requireBase = true)
    {
        bool applied = false;
        transactions.TryApply(attempt, () => applied = requests.TryApply(request, action), requireBase);
        return applied;
    }
    private void Status(ClientLocalSnapshotRequest intent, string message) => (intent.SetStatus ?? presentation.DefaultStatus)(message);
    private void Complete(ClientGameRequestTracker.Request request)
    { mutations.Reset(); recovery.Close(); requests.End(request); }
}
