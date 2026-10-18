using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

// A relocation retains its original base, destination and submission identity.
// Explicit settlement and an already-landed gravship share one network pipeline.
internal sealed class ClientColonyRelocation
{
    private const LocalMutationKind Kind = LocalMutationKind.ColonyRelocation;
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker<Boundary> requests;
    private readonly ClientSnapshotMutationAdmission admission;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotTransaction transactions;
    private readonly ClientSnapshotUploader uploader;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly ClientColonyRegistration registration;
    private readonly ClientWorldPublication publication;
    private readonly IClientColonyRelocationTransport transport;
    private readonly IClientColonyRelocationPresentation view;
    private Job? active;
    internal event Action<ModWorldMapMarkerDeliveryDto?>? MarkersAccepted;
    internal event Action? Completed;

    internal ClientColonyRelocation(ClientSessionLifecycle session, IClientWorldIdentity identity, IClientWorldCatalogRuntime runtime,
        ClientSnapshotMutationAdmission admission, ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations,
        ClientSnapshotTransaction transactions, ClientSnapshotUploader uploader, ClientSnapshotFailureRecovery recovery,
        ClientColonyRegistration registration, ClientWorldPublication publication, IClientColonyRelocationTransport transport, IClientColonyRelocationPresentation view)
    {
        this.session = session; this.admission = admission; this.synchronization = synchronization; this.mutations = mutations;
        this.transactions = transactions; this.uploader = uploader; this.recovery = recovery; this.registration = registration;
        this.publication = publication; this.transport = transport; this.view = view;
        requests = new(session, () => new(runtime.Bind(), identity.WorldId), boundary => boundary.Runtime.IsCurrent
            && (identity.WorldId == boundary.WorldId || identity.WorldId == boundary.PublishingWorldId), () => active = null);
    }
    internal ClientColonyRelocationCommand? Command(IClientColonyRelocationTarget target, string? originalFailure)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (!Connected || target.HomeCount == 0) return null;
        string? Failure() => target.HomeCount != 1 ? Text("DisabledMultipleLocalColonies")
            : target.SettleFailure ?? originalFailure ?? AdmissionFailure(target);
        string? disabled = Failure();
        return new(disabled, () =>
        {
            using var publication = ClientSessionWork.EnterPublication();
            if (!requests.TryApply(scope, static () => { }) || !target.IsCurrent) return;
            if (!target.IsAvailable) { Reject(Text("InvalidTarget")); return; }
            if (Failure() is { } failure) { Reject(failure); return; }
            string label = target.OldLabel; bool consumed = false;
            view.Confirm(label, target.Tile, () =>
            {
                using var confirm = ClientSessionWork.EnterPublication();
                if (consumed) return Task.CompletedTask; consumed = true;
                if (!requests.TryApply(scope, static () => { }) || !target.IsCurrent) return Task.CompletedTask;
                if (!target.IsAvailable) { Reject(Text("InvalidTarget")); return Task.CompletedTask; }
                if (Failure() is { } reason) { Reject(reason); return Task.CompletedTask; }
                var job = Begin(target, implicitMove: false);
                return job is null ? Task.CompletedTask : Run(job);
            });
        });
    }
    internal bool Takeoff(IClientColonyRelocationTarget source)
    {
        using var p = ClientSessionWork.EnterPublication(); requests.Capture();
        if (session.IsUnavailable) { Reject(Text("NotConfigured")); return false; }
        if (!Connected) return true;
        if (!source.IsAvailable) { Reject(Text("InvalidTarget")); return false; }
        if (AdmissionFailure(source) is { } failure) { Reject(failure); return false; }
        var job = Begin(source, implicitMove: true);
        if (job is null) return false;
        job.Stage = Stage.Flight;
        if (Status(job, "StatusFlight")) return true;
        job.Attempt.Admission.Dispose(); return false;
    }
    internal Task LandAsync(IClientColonyRelocationTarget destination)
    {
        using var p = ClientSessionWork.EnterPublication(); requests.Capture();
        if (!Connected || !destination.IsCurrent || !destination.IsAvailable) return Task.CompletedTask;
        Job? job = active;
        if (job is not null)
        {
            if (job.Stage != Stage.Flight || !Current(job)) return Task.CompletedTask;
            job.Target = destination; job.Tile = destination.Tile; job.Layer = destination.Layer; job.Stage = Stage.Preflight;
        }
        else
        {
            if (AdmissionFailure(destination) is { } failure) { Reject(failure); return Task.CompletedTask; }
            job = Begin(destination, implicitMove: true);
        }
        return job is null ? Task.CompletedTask : Run(job);
    }
    private bool Connected => session.ReadContext().IsConfigured && !string.IsNullOrWhiteSpace(session.SessionId)
        && !string.IsNullOrWhiteSpace(session.ReadContext().AuthToken) && !session.IsUnavailable;
    private string? AdmissionFailure(IClientColonyRelocationTarget target)
    {
        if (!Connected) return Text("NotConfigured");
        if (mutations.IsPending || active is not null) return Text("DisabledAtomic");
        if (synchronization.IsBusy) return Text("DisabledSync");
        if (target.HasRemoteSession) return Text("DisabledRemoteMap");
        return null;
    }
    private Job? Begin(IClientColonyRelocationTarget target, bool implicitMove)
    {
        var result = admission.Begin();
        if (result.Attempt is not { } attempt) { Reject(AdmissionText(result)); return null; }
        var request = requests.Begin(attempt.Admission);
        if (request is null) { attempt.Admission.Dispose(); return null; }
        var job = active = new Job(request, attempt, target, implicitMove);
        try
        {
            string status = "";
            if (!Step(job, () => status = Text("StatusPreflight")) || !Step(job, () => mutations.Begin(Kind, status))) return job;
            if (implicitMove) Suppress(job);
            return job;
        }
        catch { attempt.Admission.Dispose(); throw; }
    }
    private async Task Run(Job job)
    {
        if (job.Running) return;
        job.Running = true; job.Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await ClientSessionWork.Run(() => Execute(job)).ConfigureAwait(false); }
        finally
        {
            try { job.Attempt.Admission.Dispose(); }
            finally { job.Running = false; job.Idle.TrySetResult(); }
        }
    }
    private async Task Execute(Job job)
    {
        try
        {
            if (!Current(job)) return;
            if (job.Stage == Stage.Preflight)
            {
                var result = await transport.PreflightAsync(job.Attempt.Context, job.Tile, job.Key, job.Layer, job.Attempt.Owner.Token).ConfigureAwait(false);
                if (!Current(job)) return;
                if (!result.Success || result.Response?.Result?.Accepted != true)
                {
                    string? failure = result.Response?.Result?.Message ?? result.Message;
                    bool retry = ClientNetworkFailurePolicy.CanRetry(result.ErrorCode, result.Message);
                    await Publish(job, () => Failed(job, failure ?? Text("PreflightFailed"), retry)); return;
                }
                Task settled = Task.CompletedTask;
                await Publish(job, () =>
                {
                    if (job.Implicit) { job.Stage = Stage.Upload; return; }
                    if (!job.Target.IsAvailable || job.Target.HomeCount != 1 || job.Target.HasRemoteSession)
                    { Abort(job, Text("InvalidTarget")); return; }
                    if (job.Target.SettleFailure is { } failure) { Abort(job, failure); return; }
                    if (!Suppress(job) || !Status(job, "StatusSettling")) return;
                    job.LocalChanged = true; job.Stage = Stage.Settle;
                    job.Target.Settle();
                    if (!Current(job)) return;
                    settled = Publish(job, () =>
                    {
                        if (!Status(job, "StatusAbandoningOldColony")) return;
                        job.Target.AbandonOld();
                        if (Current(job)) job.Stage = Stage.Upload;
                    }, settled: true);
                }).ConfigureAwait(false);
                await settled.ConfigureAwait(false);
            }
            if (!Current(job)) return;
            if (job.Stage == Stage.Upload)
            {
                bool ready = false;
                await Publish(job, () =>
                {
                    if (!CheckAnchor(job) || !Status(job, "StatusUploadingSnapshot")) return;
                    ClientSynchronizationCoordinator.Lease? lease = null;
                    try { ready = synchronization.TryAcquireSnapshot(out lease, job.Attempt.Admission); }
                    finally
                    {
                        if (lease is not null)
                        { job.Attempt = job.Attempt with { Admission = lease }; requests.Track(job.Request, lease); }
                    }
                    if (!ready) Failed(job, view.Text("ClashOfRim.SnapshotUpload.Busy"), true);
                }).ConfigureAwait(false);
                if (!ready || !Current(job)) return;
                var uploaded = await uploader.UploadAsync(false, "ColonyRelocation", ModSnapshotUploadKinds.ColonyRelocation, null,
                    job.Attempt.Admission, job.Attempt.Owner.Token, new(() => Current(job), id => job.PublishingSnapshot = id)).ConfigureAwait(false);
                if (!Current(job)) return;
                await Publish(job, () =>
                {
                    if (uploaded.Failure is { } failure)
                    { Failed(job, view.SnapshotFailure(failure), job.PublishingSnapshot is null && ClientNetworkFailurePolicy.CanRetry(failure.Code, failure.Message)); return; }
                    if (string.IsNullOrWhiteSpace(uploaded.AcceptedSnapshotId) || session.ReadContext().CurrentSnapshotId != uploaded.AcceptedSnapshotId) return;
                    job.Relocated = uploaded.AcceptedSnapshotId;
                    job.Attempt = job.Attempt with { Context = session.ReadContext() };
                    job.PublishingSnapshot = null; job.Stage = Stage.Confirm;
                }).ConfigureAwait(false);
            }
            if (!Current(job) || job.Stage != Stage.Confirm) return;
            bool valid = false;
            ClientWorldPublication.Observation? observation = null;
            await Publish(job, () =>
            {
                valid = CheckAnchor(job) && Status(job, "StatusConfirming");
                if (valid) observation = publication.Capture();
            }).ConfigureAwait(false);
            if (!valid || !Current(job)) return;
            var confirmed = await transport.ConfirmAsync(job.Attempt.Context, job.Base, job.Relocated!, job.Tile, job.Key, job.Layer, job.Attempt.Owner.Token).ConfigureAwait(false);
            if (!Current(job)) return;
            if (!confirmed.Success || confirmed.Response?.Result?.Accepted != true)
            {
                string? failure = confirmed.Response?.Result?.Message ?? confirmed.Message;
                bool retry = ClientNetworkFailurePolicy.CanRetry(confirmed.ErrorCode, confirmed.Message);
                await Publish(job, () => Failed(job, failure ?? Text("ConfirmFailed"), retry)); return;
            }
            var accepted = ClientProtocolJson.Copy(confirmed.Response);
            job.ConfirmAccepted = true;
            await Publish(job, () =>
            {
                if (!CheckAnchor(job)) return;
                job.Request.Scope.Boundary.PublishingWorldId = accepted.WorldConfiguration?.WorldConfigurationId?.Trim();
                if (!publication.Publish(observation!, accepted.WorldConfiguration, ClientWorldProjection.Sites, () => Current(job),
                    ("colony-relocation", accepted.WorldMapMarkers))) return;
                foreach (Action<ModWorldMapMarkerDeliveryDto?> handler in MarkersAccepted?.GetInvocationList() ?? Array.Empty<Delegate>())
                    if (!Step(job, () => handler(ClientProtocolJson.Copy(accepted.WorldMapMarkers)))) return;
                if (!Step(job, () => registration.Invalidate(ClientColonyRegistrationKind.Sites))
                    || !Step(job, () => registration.Suppressed = false) || !Step(job, mutations.Reset) || !Step(job, recovery.Close)) return;
                string message = Text("Succeeded");
                if (!Step(job, () => view.Message(message, ClientWorldWriteMessage.Success))) return;
                Finish(job);
                foreach (Action handler in Completed?.GetInvocationList() ?? Array.Empty<Delegate>())
                {
                    bool notified = false;
                    requests.TryApply(job.Request.Scope, () =>
                    {
                        if (!session.IsUnavailable && job.Target.IsCurrent && session.ReadContext().CurrentSnapshotId == job.Attempt.Context.CurrentSnapshotId)
                        { handler(); notified = true; }
                    });
                    if (!notified) return;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Publish(job, () =>
            {
                if (!Step(job, () => view.Diagnose("Colony relocation " + job.Stage + ": " + ex))) return;
                Failed(job, ex.GetType().Name + " " + ex.Message, job.Stage is Stage.Preflight or Stage.Upload or Stage.Confirm
                    && !job.ConfirmAccepted && job.PublishingSnapshot is null && ClientNetworkFailurePolicy.CanRetry(ex));
            }).ConfigureAwait(false);
        }
    }
    private bool CheckAnchor(Job job)
    {
        if (job.Target.IsSingleDestination) return Current(job);
        Failed(job, Text("LocalAnchorInvalid", ("COUNT", job.Target.HomeCount)), false); return false;
    }
    private void Failed(Job job, string message, bool canRetry)
    {
        if (!Current(job)) return;
        if (!job.LocalChanged) { Abort(job, message); return; }
        // Retry owns this exact stage. It never settles or abandons twice.
        recovery.Show(Kind, message, () => _ = Retry(job), canRetry);
    }
    private Task Retry(Job job)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!Apply(job, static () => { }, lease: false, requireSnapshot: false)) return Task.CompletedTask;
        if (job.Running)
        {
            // The failure window can consume retry before the publishing worker's
            // finally releases admission. Keep that decision and resume on the game.
            Task idle = job.Idle.Task;
            return ClientSessionWork.Run(async () =>
            {
                await idle.WaitAsync(job.Attempt.Owner.Token).ConfigureAwait(false);
                Task next = Task.CompletedTask;
                await ClientGamePublication.EnqueueAsync(job.Attempt.Game.Enqueue, job.Attempt.Owner.Token,
                    run => Apply(job, run, lease: false, requireSnapshot: false), () => next = Retry(job)).ConfigureAwait(false);
                await next.ConfigureAwait(false);
            });
        }
        var result = admission.Retry(job.Attempt, Kind);
        if (result.Attempt is not { } attempt)
        {
            if (result.Failure != ClientSnapshotAdmissionFailure.Obsolete)
                recovery.Show(Kind, AdmissionText(result), () => _ = Retry(job), result.Failure == ClientSnapshotAdmissionFailure.Busy);
            return Task.CompletedTask;
        }
        job.Attempt = attempt; requests.Track(job.Request, attempt.Admission);
        return Run(job);
    }
    private void Abort(Job job, string message)
    {
        if (!Step(job, mutations.Reset) || !Step(job, () => registration.Suppressed = false)) return;
        Step(job, () => Reject(message)); Finish(job);
    }
    private void Finish(Job job)
    { if (Current(job)) { job.Stage = Stage.Complete; active = null; requests.End(job.Request); } }
    private bool Suppress(Job job) => Step(job, () => registration.Suppressed = true)
        && Step(job, () => registration.Invalidate(ClientColonyRegistrationKind.Sites));
    private bool Status(Job job, string key)
    {
        string status = "";
        return Step(job, () => status = Text(key)) && Step(job, () => mutations.UpdateStatus(status));
    }
    private bool Current(Job job, bool lease = true) => Apply(job, static () => { }, lease);
    private bool Apply(Job job, Action action, bool lease = true, bool requireSnapshot = true)
    {
        bool applied = false;
        requests.TryApply(job.Request, () =>
        {
            if (!ReferenceEquals(active, job) || session.IsUnavailable || !job.Target.IsCurrent) return;
            string? snapshot = session.ReadContext().CurrentSnapshotId;
            if (requireSnapshot && snapshot != job.Attempt.Context.CurrentSnapshotId && snapshot != job.PublishingSnapshot) return;
            if (lease) applied = transactions.TryApply(job.Attempt, action, requireBaseSnapshot: job.PublishingSnapshot is null);
            else { action(); applied = true; }
        });
        return applied;
    }
    private bool Step(Job job, Action action) => Apply(job, action) && Current(job);
    private Task Publish(Job job, Action action, bool settled = false)
    {
        bool consumed = false; Exception? failure = null;
        void Enqueue(Action run, Action discard)
        {
            if (settled) job.Target.EnqueueSettled(run, ex => { failure = ex; run(); });
            else job.Attempt.Game.Enqueue(run, () => { consumed = true; discard(); });
        }
        return ClientGamePublication.EnqueueAsync(Enqueue, job.Attempt.Owner.Token, run => Apply(job, run), () =>
        {
            if (consumed) return; consumed = true;
            if (failure is not null) throw failure;
            action();
        });
    }
    private string AdmissionText(ClientSnapshotAdmissionResult result) => result.Failure switch
    {
        ClientSnapshotAdmissionFailure.Busy => Text("DisabledSync"),
        ClientSnapshotAdmissionFailure.BaseSnapshotChanged => view.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged"),
        _ => result.Admission == LocalMutationAdmission.SnapshotMissing ? Text("MissingSnapshot")
            : result.Admission == LocalMutationAdmission.PendingMutation && mutations.Current is { } pending ? view.Blocked(pending) : Text("NotConfigured")
    };
    private string Text(string key, params (string Name, object? Value)[] args) => view.Text("ClashOfRim.ColonyRelocation." + key, args);
    private void Reject(string message) => view.Message(message, ClientWorldWriteMessage.Reject);
    private enum Stage { Flight, Preflight, Settle, Upload, Confirm, Complete }
    private sealed record Boundary(IClientWorldCatalogSession Runtime, string WorldId) { internal string? PublishingWorldId; }
    private sealed class Job
    {
        internal Job(ClientRequestTracker<Boundary>.Request request, ClientSnapshotTransaction.Attempt attempt, IClientColonyRelocationTarget target, bool implicitMove)
        {
            Request = request; Attempt = attempt; Target = target; Implicit = LocalChanged = implicitMove;
            Base = attempt.Context.CurrentSnapshotId!; Tile = target.Tile; Layer = target.Layer;
            Key = "colony-relocation:" + attempt.Context.UserId + ":" + attempt.Context.ColonyId + ":" + Base + ":" + Guid.NewGuid().ToString("N");
        }
        internal readonly ClientRequestTracker<Boundary>.Request Request;
        internal ClientSnapshotTransaction.Attempt Attempt;
        internal IClientColonyRelocationTarget Target;
        internal readonly bool Implicit;
        internal readonly string Base, Key;
        internal bool LocalChanged, Running, ConfirmAccepted;
        internal TaskCompletionSource Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Tile, Layer;
        internal Stage Stage = Stage.Preflight;
        internal string? PublishingSnapshot, Relocated;
    }
}
