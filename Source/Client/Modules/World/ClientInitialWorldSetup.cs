using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientInitialWorldEffect { Markers, Events, Players, Chat, ColonySites, Extensions, Presence }
internal interface IClientInitialWorldRuntime
{
    bool IsPlaying { get; }
    bool IsPresenceRunning { get; }
    void Enqueue(Action action, Action discard);
}
internal interface IClientInitialWorldPresentation : IClientWorldWritePresentation
{
    void SnapshotStatus(string text);
    string SnapshotFailure(SnapshotUploadFailure failure);
}

// One initial setup receipt owns metadata, substrate, administrator catalogs and
// optional in-game snapshot completion. Other domains receive guarded notifications.
internal sealed class ClientInitialWorldSetup
{
    private sealed record Boundary(ClientWorldBaselineSubmission.Captured World, ClientRequestTracker.Scope Setup);
    private readonly ClientRequestTracker<Boundary> requests;
    private readonly ClientSessionLifecycle session;
    private readonly ClientWorldSetupState setup;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientWorldBaselineCapture capture;
    private readonly ClientWorldBaselineSubmission submission;
    private readonly ClientAdminBaselineCapture adminCapture;
    private readonly IClientAdminBaselineTransport adminTransport;
    private readonly ClientSnapshotUploader snapshots;
    private readonly IClientInitialWorldRuntime runtime;
    private readonly IClientInitialWorldPresentation presentation;
    internal event Action<ClientInitialWorldEffect>? EffectRequested;

    internal ClientInitialWorldSetup(ClientSessionLifecycle session, ClientWorldSetupState setup, ClientSynchronizationCoordinator synchronization,
        ClientWorldBaselineCapture capture, ClientWorldBaselineSubmission submission, ClientAdminBaselineCapture adminCapture,
        IClientAdminBaselineTransport adminTransport, ClientSnapshotUploader snapshots, IClientInitialWorldRuntime runtime, IClientInitialWorldPresentation presentation)
    {
        this.session = session; this.setup = setup; this.synchronization = synchronization; this.capture = capture;
        this.submission = submission; this.adminCapture = adminCapture; this.adminTransport = adminTransport;
        this.snapshots = snapshots; this.runtime = runtime; this.presentation = presentation;
        requests = new(session, () => new(submission.Capture(), setup.CaptureScope()),
            boundary => submission.IsCurrent(boundary.World) && setup.IsCurrent(boundary.Setup), static () => { });
    }

    internal Task StartAsync(ModColonyAppearanceDto? appearance)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture(); // Retire obsolete work before acquiring shared admission.
        var initial = setup.TakeInitial();
        if (initial is null) return Task.CompletedTask;
        var source = submission.Capture();
        if (!submission.IsCurrent(source)) { setup.RestoreInitial(initial); return Task.CompletedTask; }
        if (!source.Context.IsConfigured)
        {
            Report("ClashOfRim.WorldBaseline.StatusConfigMissing"); return Task.CompletedTask;
        }
        if (!synchronization.TryBeginManual(out var admission))
        {
            setup.RestoreInitial(initial); Report("ClashOfRim.Sync.StatusBusy"); return Task.CompletedTask;
        }
        var request = requests.Begin(admission);
        if (request is null) { admission.Dispose(); setup.RestoreInitial(initial); return Task.CompletedTask; }
        return ExecuteAsync(request, initial, source, appearance);

        void Report(string key)
        {
            string text = presentation.Text(key);
            requests.TryApply(scope, () => presentation.Status(text));
        }
    }

    private async Task ExecuteAsync(ClientRequestTracker<Boundary>.Request request, ClientRequestTracker.Scope initial,
        ClientWorldBaselineSubmission.Captured source, ModColonyAppearanceDto? appearance)
    {
        ClientWorldBaselineSubmission.Prepared? prepared = null;
        string? publishingSnapshot = null;
        string? uploadBaseSnapshot = null;
        bool snapshotAccepted = false;
        bool sending = false, uploading = false;
        bool Current() => Apply(static () => { });
        bool Apply(Action action)
        {
            bool applied = false;
            requests.TryApply(request, () =>
            {
                if (session.IsUnavailable || request.Admission?.IsCurrent == false || (prepared is not null && !submission.IsCurrent(prepared))) return;
                if (uploading && (!runtime.IsPlaying || string.IsNullOrWhiteSpace(session.SessionId))) return;
                if (publishingSnapshot is not null)
                {
                    string? snapshot = session.ReadContext().CurrentSnapshotId;
                    if (snapshot != publishingSnapshot && (snapshotAccepted || snapshot != uploadBaseSnapshot)) return;
                }
                action(); applied = true;
            });
            return applied;
        }
        bool Step(Action action) => Apply(action) && Current();
        void Notice(string key, bool snapshot = false, ClientWorldWriteMessage? message = null, params (string Name, object? Value)[] args)
        {
            string text = "";
            if (!Step(() => text = presentation.Text(key, args))
                || !Step(() => { if (snapshot) presentation.SnapshotStatus(text); else presentation.Status(text); })) return;
            if (message is { } value) Apply(() => presentation.Message(text, value));
        }
        Task Publish(Action action)
        {
            bool consumed = false;
            return ClientGamePublication.EnqueueAsync((run, discard) => runtime.Enqueue(run, () => { consumed = true; discard(); }),
                request.Scope.Owner.Token, Apply, () => { if (consumed) return; consumed = true; action(); });
        }
        bool Notify(ClientInitialWorldEffect effect)
        {
            foreach (Action<ClientInitialWorldEffect> handler in EffectRequested?.GetInvocationList() ?? Array.Empty<Delegate>())
                if (!Step(() => handler(effect))) return false;
            return Current();
        }
        try
        {
            if (!Current()) return;
            var captured = capture.Capture(source, appearance, current: Current);
            prepared = captured.Prepared;
            if (!Current()) return;
            if (captured.Error is { } invalid)
            {
                string reason = "";
                if (Step(() => reason = presentation.CaptureFailure(invalid)))
                    Notice("ClashOfRim.WorldBaseline.StatusInvalid", message: ClientWorldWriteMessage.Reject, args: new[] { ("REASON", (object?)reason) });
                return;
            }
            if (prepared is null) { setup.RestoreInitial(initial); return; }
            var adminOwner = adminCapture.Capture();
            Notice("ClashOfRim.WorldBaseline.StatusSubmitting");
            sending = true;
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                var world = await submission.SubmitAsync(prepared, Current).ConfigureAwait(false);
                if (world is null || !Current()) return;
                if (!world.Success || world.Response?.Result?.Accepted != true)
                { await Failed(world, world.Response?.Result, "ClashOfRim.WorldBaseline.StatusWorldConfigFailed", false); return; }
                var requirements = await adminTransport.RequirementsAsync(prepared.Context, request.Scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return;
                if (!requirements.Success || requirements.Response?.Result?.Accepted != true)
                { await Failed(requirements, requirements.Response?.Result, "ClashOfRim.WorldBaseline.StatusAdminBaselineFailed", true); return; }
                var baseline = await adminCapture.CaptureAsync(adminOwner, requirements.Response.BaselineExtensions,
                    request.Scope.Owner.Token, Current).ConfigureAwait(false);
                if (!Current()) return;
                var submitted = await adminTransport.SubmitAsync(prepared.Context, baseline, request.Scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return;
                if (!submitted.Success || submitted.Response?.Result?.Accepted != true)
                { await Failed(submitted, submitted.Response?.Result, "ClashOfRim.WorldBaseline.StatusAdminBaselineFailed", true); return; }
                var accepted = ClientProtocolJson.Copy(submitted.Response);
                Task? upload = null;
                await Publish(() => submission.Complete(prepared, () =>
                {
                    Notice("ClashOfRim.WorldBaseline.StatusSubmitted", message: ClientWorldWriteMessage.Success,
                        args: new[] { ("PRICECOUNT", (object?)accepted.StandardMarketValueCount), ("TRAPAUTO", (object?)accepted.TrapAutoApprovedCount), ("TRAPCANDIDATE", (object?)accepted.TrapCandidateCount) });
                    if (!Current() || !Notify(ClientInitialWorldEffect.Markers)) return;
                    if (runtime.IsPlaying && !string.IsNullOrWhiteSpace(session.SessionId) && Current()) upload = UploadSnapshot();
                })).ConfigureAwait(false);
                if (upload is not null) await upload.ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            void Failure()
            {
                Notice(uploading ? "ClashOfRim.WorldBaseline.StatusInitialSnapshotException" : sending ? "ClashOfRim.WorldBaseline.StatusSubmitException" : "ClashOfRim.WorldBaseline.StatusInvalid",
                    snapshot: uploading, message: uploading ? ClientWorldWriteMessage.Reject : null,
                    args: new[] { ("TYPE", (object?)ex.GetType().Name), ("MESSAGE", (object?)ex.Message), ("REASON", (object?)ex.Message) });
                Apply(() => presentation.Diagnose("Initial world setup failed: " + ex));
            }
            if (sending) await Publish(Failure).ConfigureAwait(false); else Failure();
        }
        finally { if (prepared is not null) submission.Discard(prepared); requests.End(request); }

        Task Failed<T>(ClashOfRimClientNetworkResult<T> result, ModProtocolResponseDto? rejection, string key, bool notify)
        {
            string code = result.ErrorCode ?? rejection?.ErrorCode.ToString() ?? "";
            string message = result.Message ?? rejection?.Message ?? "";
            return Publish(() =>
            {
                Notice(key, message: notify ? ClientWorldWriteMessage.Reject : null,
                    args: new[] { ("CODE", (object?)code), ("MESSAGE", (object?)message) });
                Apply(() => presentation.Diagnose("Initial world setup rejected: " + code + ": " + message));
            });
        }

        async Task UploadSnapshot()
        {
            ClientSynchronizationCoordinator.Lease? admission = null;
            bool acquired;
            try { acquired = synchronization.TryAcquireSnapshot(out admission, request.Admission); }
            finally
            {
                // Promotion observers can reset setup or throw after allocating the lease.
                // Adopt its successor even on that exception so the operation's finally releases it.
                if (admission is { } promoted)
                {
                    if (requests.TryApply(request, () => requests.Track(request, promoted))) uploading = true;
                    else { promoted.Dispose(); admission = null; }
                }
            }
            if (!acquired) { Notice("ClashOfRim.Menu.UploadSnapshotBusy", snapshot: true); return; }
            if (admission is null) return;
            Notice("ClashOfRim.WorldBaseline.StatusUploadingInitialSnapshot", snapshot: true);
            if (!Current()) return;
            uploadBaseSnapshot = session.ReadContext().CurrentSnapshotId;
            var result = await snapshots.UploadAsync(false, null, ModSnapshotUploadKinds.InitialColonySnapshot, null,
                admission, request.Scope.Owner.Token, new(Current, id => publishingSnapshot = id)).ConfigureAwait(false);
            snapshotAccepted = result.Failure is null;
            if (!Current()) return;
            await Publish(() =>
            {
                if (result.Failure is { } failure)
                {
                    string message = "";
                    if (Step(() => message = presentation.SnapshotFailure(failure)))
                        Notice("ClashOfRim.WorldBaseline.StatusInitialSnapshotFailed", snapshot: true, message: ClientWorldWriteMessage.Reject,
                            args: new[] { ("CODE", (object?)failure.Code), ("MESSAGE", (object?)message) });
                    Apply(() => presentation.Diagnose("Initial colony snapshot upload failed: " + failure.Code + ": " + message));
                    return;
                }
                if (session.ReadContext().CurrentSnapshotId != result.AcceptedSnapshotId) return;
                Notice("ClashOfRim.WorldBaseline.StatusInitialSnapshotUploaded", snapshot: true, message: ClientWorldWriteMessage.Success,
                    args: new[] { ("SNAPSHOT", (object?)result.AcceptedSnapshotId) });
                if (!Current()) return;
                // Follow-up domains may themselves need synchronization admission.
                request.Admission = null; admission.Dispose();
                foreach (var effect in new[] { ClientInitialWorldEffect.Events, ClientInitialWorldEffect.Players, ClientInitialWorldEffect.Chat,
                    ClientInitialWorldEffect.ColonySites, ClientInitialWorldEffect.Extensions })
                    if (!Notify(effect)) return;
                if (!runtime.IsPresenceRunning) Notify(ClientInitialWorldEffect.Presence);
            }).ConfigureAwait(false);
        }
    }
}
