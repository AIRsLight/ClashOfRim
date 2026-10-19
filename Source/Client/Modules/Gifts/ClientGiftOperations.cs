using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Gifts;

internal sealed class ClientGiftOperations
{
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly IClientGiftTransport transport;
    private readonly IClientGiftPresentation presentation;
    private readonly Func<ClientGiftCapabilities> capabilities;

    internal ClientGiftOperations(ClientSnapshotMutationAdmission admission, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientSnapshotTransaction transactions, ClientSnapshotFailureRecovery recovery,
        IClientGiftTransport transport, IClientGiftPresentation presentation, Func<ClientGiftCapabilities> capabilities)
    {
        this.admission = admission; this.synchronization = synchronization; this.mutations = mutations;
        this.transactions = transactions; this.recovery = recovery; this.transport = transport;
        this.presentation = presentation; this.capabilities = capabilities;
    }

    internal event Action? Completed;
    internal bool CanStartArrival => admission.CanBegin;

    internal Task SendAsync(IClientGiftSource source, ClientGiftTarget target, bool forced, string? message)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var policy = capabilities();
        if (ClientGiftTargets.DisabledReason(policy, forced) is { } disabled)
        { Reject(presentation.Text(disabled)); return Task.CompletedTask; }
        var attempt = AcceptAdmission(admission.Begin());
        if (attempt is null) return Task.CompletedTask;
        bool handedOff = false;
        try
        {
            var cargo = source.Describe();
            if (!ClientGiftTargets.HasContext(target.UserId, target.ColonyId, target.MapId))
            { Reject(Text("StatusTargetIncomplete")); return Task.CompletedTask; }
            if (cargo.Tile != target.Tile || cargo.TileLayerId != target.TileLayerId)
            { Reject(Text("StatusTargetTileMismatch")); return Task.CompletedTask; }
            if (!cargo.HasCargo) { Reject(Text("StatusNoSelectedItems")); return Task.CompletedTask; }
            string prefix = cargo.Kind == ClientGiftSourceKind.Caravan ? "gift-caravan" : "gift-transport-pods";
            string key = $"{prefix}:{attempt.Context.UserId}:{attempt.Context.CurrentSnapshotId}:{cargo.Key}";
            if (cargo.Kind == ClientGiftSourceKind.Caravan) key += ":" + Guid.NewGuid().ToString("N");
            var job = new Job(source, cargo, target, forced, string.IsNullOrWhiteSpace(message) ? null : message, key);
            if (cargo.Kind == ClientGiftSourceKind.TransportPods)
            {
                mutations.Begin(LocalMutationKind.GiftSend, Text("StatusPackagingSnapshot"));
                job.Removed = true; // The engine removes the voyage immediately after Arrived returns.
            }
            handedOff = true;
            return RunSourceAsync(attempt, job);
        }
        finally { if (!handedOff) attempt.Admission.Dispose(); }
    }

    private async Task RunSourceAsync(ClientSnapshotTransaction.Attempt attempt, Job job)
    {
        Task submitted = Task.CompletedTask;
        try
        {
            if (job.Cargo.Kind == ClientGiftSourceKind.TransportPods)
                await ClientGamePublication.EnqueueAsync(attempt.Game.Enqueue, attempt.Owner.Token,
                    action => transactions.TryApply(attempt, action), () => submitted = CaptureAndSubmit(attempt, job));
            else submitted = CaptureAndSubmit(attempt, job);
            await submitted;
        }
        catch (Exception ex)
        {
            transactions.TryApply(attempt, () =>
            {
                string error = ExceptionText(ex);
                presentation.Status(error); presentation.Diagnose(ex);
                if (job.Removed) recovery.Show(LocalMutationKind.GiftSend, error, static () => { }, canRetry: false);
            });
        }
        finally { attempt.Admission.Dispose(); }
    }

    private Task CaptureAndSubmit(ClientSnapshotTransaction.Attempt attempt, Job job)
    {
        try
        {
            if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
            if (job.Command is null)
            {
                var capture = job.Source.Capture(attempt.Context, job.Forced);
                if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
                if (capture.Rejection is not null || capture.Things.Count == 0)
                {
                    string error = capture.Rejection ?? Text("StatusNoSelectedItems");
                    Reject(error);
                    if (job.Removed) recovery.Show(LocalMutationKind.GiftSend, error, static () => { }, canRetry: false);
                    return Task.CompletedTask;
                }
                // Freeze nested pawn/thing payloads once. UI/source references must not change a retry's intent.
                var things = ClientProtocolJson.Deserialize<List<ModThingReferenceDto>>(
                    ClientProtocolJson.Serialize(capture.Things.ToList()), type => "Invalid captured gift payload: " + type);
                job.Command = new(job.Key, job.Target, job.Forced, job.Message,
                    job.Cargo.Kind == ClientGiftSourceKind.Caravan ? "MapEdge" : "DropPod", things);
            }
            if (!job.Removed)
            {
                mutations.Begin(LocalMutationKind.GiftSend, Text("StatusReserving"));
                job.Removed = true;
                var removed = job.Source.Remove();
                if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
                if (!removed.Success)
                {
                    string error = Text("StatusLocalRemoveFailed", ("MESSAGE", removed.Message));
                    presentation.Status(error);
                    recovery.Show(LocalMutationKind.GiftSend, error, static () => { }, canRetry: false);
                    return Task.CompletedTask;
                }
                job.RemovalMessage = removed.Message;
                job.LocalPrepared = true;
            }
            else if (job.Cargo.Kind == ClientGiftSourceKind.TransportPods)
                job.RemovalMessage = Text("ReasonTransportPodGift");
            return PrepareAndSubmit(attempt, job);
        }
        catch (Exception ex)
        {
            transactions.TryApply(attempt, () =>
            {
                string error = ExceptionText(ex);
                presentation.Status(error); presentation.Diagnose(ex);
                if (job.Removed) recovery.Show(LocalMutationKind.GiftSend, error,
                    () => _ = Retry(attempt, job), canRetry: job.LocalPrepared || job.Cargo.Kind == ClientGiftSourceKind.TransportPods);
            });
            return Task.CompletedTask;
        }
    }

    private Task PrepareAndSubmit(ClientSnapshotTransaction.Attempt attempt, Job job)
    {
        mutations.Begin(LocalMutationKind.GiftSend, Text("StatusPackagingSnapshot"));
        if (job.Package is null || job.Payload is null)
        {
            bool built = ClientSnapshotPreparation.TryBuild(attempt.Game, attempt.Context.UserId, attempt.Context.ColonyId,
                out var build, out var failure);
            if (!transactions.TryApply(attempt, static () => { })) return Task.CompletedTask;
            if (!built)
            {
                string error = Text("StatusSnapshotBuildFailed", ("REASON", failure?.Message));
                presentation.Status(error);
                recovery.Show(LocalMutationKind.GiftSend, error, () => _ = Retry(attempt, job), canRetry: true);
                return Task.CompletedTask;
            }
            job.Package = build.Package!; job.Payload = build.Payload!;
        }
        if (!synchronization.TryAcquireSnapshot(out var upload, attempt.Admission))
        { Reject(presentation.Text("ClashOfRim.SnapshotUpload.Busy")); return Task.CompletedTask; }
        attempt = attempt with { Admission = upload };
        mutations.Begin(LocalMutationKind.GiftSend, Text("StatusSubmittingTransaction"));
        presentation.Status(Text(job.Forced ? "StatusSubmittingForced" : "StatusSubmittingNormal", ("TARGET", job.Target.Label)));
        return ClientSessionWork.Run(() => transactions.ExecuteAsync(attempt,
            () => transport.SubmitAsync(attempt.Context, job.Command!, job.Package!, job.Payload!, attempt.Owner.Token), r => r.Result,
            failure =>
            {
                string error = failure.Exception is { } ex ? ExceptionText(ex) : Text(
                    failure.Rejection is null ? "StatusCreateFailed" : "StatusCreateRejected",
                    ("CODE", (object?)failure.Rejection?.ErrorCode ?? failure.Network?.ErrorCode),
                    ("MESSAGE", failure.Rejection?.Message ?? failure.Network?.Message));
                presentation.Status(error);
                if (failure.Exception is not null) presentation.Diagnose(failure.Exception);
                recovery.Show(LocalMutationKind.GiftSend, error, () => _ = Retry(attempt, job), canRetry: !job.Publishing && failure.CanRetry);
            }, response =>
            {
                job.Publishing = true;
                transactions.Publish(attempt, response.AppliedSnapshotId, response.NextLineageToken);
                transactions.TryApply(attempt, () =>
                {
                    string completed = Text(job.Forced ? "StatusForcedCreated" : "StatusNormalCreated",
                        ("EVENTID", response.EventId), ("MESSAGE", job.RemovalMessage));
                    presentation.Status(completed); presentation.Message(completed, rejected: false, forced: job.Forced);
                    transactions.TryApply(attempt, () => { mutations.Reset(); recovery.Close(); }, requireBaseSnapshot: false);
                }, requireBaseSnapshot: false);
                if (transactions.TryApply(attempt, static () => { }, requireBaseSnapshot: false))
                { attempt.Admission.Dispose(); Completed?.Invoke(); }
            }));
    }

    private Task Retry(ClientSnapshotTransaction.Attempt original, Job job)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var admitted = admission.Retry(original, LocalMutationKind.GiftSend);
        if (admitted.Failure == ClientSnapshotAdmissionFailure.Obsolete) return Task.CompletedTask;
        if (admitted.Failure == ClientSnapshotAdmissionFailure.BaseSnapshotChanged)
        {
            string error = presentation.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged");
            presentation.Status(error);
            recovery.Show(LocalMutationKind.GiftSend, error, static () => { }, canRetry: false);
            return Task.CompletedTask;
        }
        var attempt = AcceptAdmission(admitted);
        if (attempt is null) return Task.CompletedTask;
        try { return CaptureAndSubmit(attempt, job); }
        finally { attempt.Admission.Dispose(); }
    }

    private ClientSnapshotTransaction.Attempt? AcceptAdmission(ClientSnapshotAdmissionResult result)
    {
        if (result.Failure == ClientSnapshotAdmissionFailure.Admission)
            Reject(presentation.Admission(result.Admission, mutations.Current));
        else if (result.Attempt is null) Reject(presentation.Text("ClashOfRim.SnapshotUpload.Busy"));
        return result.Attempt;
    }
    private string Text(string key, params (string Name, object? Value)[] arguments) => presentation.Text("ClashOfRim.GiftDelivery." + key, arguments);
    private string ExceptionText(Exception ex) => Text("StatusCreateException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
    private void Reject(string message) { presentation.Status(message); presentation.Message(message, rejected: true); }

    private sealed class Job
    {
        internal readonly IClientGiftSource Source;
        internal readonly ClientGiftCargo Cargo;
        internal readonly ClientGiftTarget Target;
        internal readonly bool Forced;
        internal readonly string? Message;
        internal readonly string Key;
        internal ClientGiftCommand? Command;
        internal bool Removed, LocalPrepared, Publishing;
        internal string RemovalMessage = string.Empty;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
        internal Job(IClientGiftSource source, ClientGiftCargo cargo, ClientGiftTarget target, bool forced, string? message, string key)
        { Source = source; Cargo = cargo; Target = target; Forced = forced; Message = message; Key = key; }
    }
}
