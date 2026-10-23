using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.Compatibility;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientSessionStartKind { Manual, Map }
internal interface IClientSessionStartupRuntime
{
    IClientSessionStartupSession Bind();
    void Enqueue(Action action, Action discard);
    bool IsPresenceRunning { get; }
}
internal interface IClientSessionStartupSession
{
    bool IsCurrent { get; }
    bool IsPlaying { get; }
    bool HasMap { get; }
    bool HasRaidBattle { get; }
    bool IsObserving { get; }
}
internal interface IClientSessionStartupPresentation
{
    string Text(string key, params (string Name, object? Value)[] args);
    string Admission(LocalMutationAdmission admission, LocalMutationState? mutation);
    string SnapshotFailure(SnapshotUploadFailure failure);
    void LoginStatus(string text);
    void SnapshotStatus(string text);
    void Message(string text);
    void CompatibilityMismatch(ModLoginResponseDto response);
    void Diagnose(string text);
}

internal sealed class ClientSessionStartup
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientWorldSourceGame sourceGame;
    private readonly ClientWorldLoginPublication publication;
    private readonly ClientSessionLogin login;
    private readonly ClientSnapshotUploader snapshots;
    private readonly IClientSessionStartupRuntime runtime;
    private readonly IClientSessionStartupPresentation view;
    private Job? active;
    private readonly IClientSessionFollowups followups;
    internal ClientSessionStartup(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization, ClientLocalMutationCoordinator mutations, ClientWorldSourceGame sourceGame,
        ClientWorldLoginPublication publication, ClientSessionLogin login, ClientSnapshotUploader snapshots,
        IClientSessionStartupRuntime runtime, IClientSessionStartupPresentation view, IClientSessionFollowups followups)
    {
        this.session = session; this.synchronization = synchronization; this.mutations = mutations; this.sourceGame = sourceGame; this.publication = publication;
        this.login = login; this.snapshots = snapshots; this.runtime = runtime; this.view = view;
        this.followups = followups;
    }
    internal bool IsInActiveWorld => session.HasSnapshotSession(session.ReadContext()) && !runtime.Bind().IsObserving;
    internal AutomaticStartup CreateAutomatic(Func<bool> currentGame) => new(this, currentGame);
    // One opportunity per source Game. Eligibility denials leave it available;
    // admitted failures still require an explicit user retry, as before.
    internal sealed class AutomaticStartup
    {
        private readonly ClientSessionStartup owner;
        private readonly Func<bool> currentGame;
        private bool attempted;
        internal AutomaticStartup(ClientSessionStartup owner, Func<bool> currentGame)
        { this.owner = owner; this.currentGame = currentGame; }
        internal Task PollAsync()
        {
            using var p = ClientSessionWork.EnterPublication();
            if (attempted || !currentGame()) return Task.CompletedTask;
            var source = owner.publication.Capture();
            if (!owner.CanStartAutomatic || !currentGame() || !owner.publication.IsCurrent(source)) return Task.CompletedTask;
            attempted = true;
            return owner.StartAsync(ClientSessionStartKind.Map);
        }
    }
    internal bool CanStartAutomatic
    {
        get
        {
            using var p = ClientSessionWork.EnterPublication();
            var context = session.ReadContext(); var bound = runtime.Bind();
            // A denied pending mutation must not consume the game's one automatic
            // startup opportunity. Use the same map admission as StartAsync.
            return mutations.CheckAdmission(context.IsConfigured, hasSnapshot: true) == LocalMutationAdmission.Allowed
                && bound.IsPlaying && bound.HasMap && !synchronization.IsBusy
                && !session.IsUnavailable && !sourceGame.BlocksCurrent
                && !bound.HasRaidBattle && !bound.IsObserving && string.IsNullOrWhiteSpace(session.SessionId);
        }
    }
    internal Task StartAsync(ClientSessionStartKind kind)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (active is { } old && !Current(old)) { old.Admission.Dispose(); active = null; }
        // Marker cache ownership survives snapshot/password changes. Capture the
        // request context separately without replacing its observation sequence.
        var source = publication.Capture(); var bound = runtime.Bind(); var context = session.ReadContext();
        bool SourceCurrent() => publication.IsCurrent(source) && bound.IsCurrent;
        if (!SourceCurrent()) return Task.CompletedTask;
        string? denial = null;
        if (kind == ClientSessionStartKind.Map && (!bound.IsPlaying || !bound.HasMap)) denial = "ClashOfRim.MapSession.StatusNoMap";
        else if (kind == ClientSessionStartKind.Map && !context.IsConfigured) denial = "ClashOfRim.MapSession.StatusNotConfigured";
        else
        {
            var admit = mutations.CheckAdmission(context.IsConfigured, kind == ClientSessionStartKind.Map || !string.IsNullOrWhiteSpace(context.CurrentSnapshotId));
            if (admit != LocalMutationAdmission.Allowed)
            {
                string reason = view.Admission(admit, mutations.Current);
                if (SourceCurrent()) view.LoginStatus(reason);
                return Task.CompletedTask;
            }
        }
        if (denial is not null) { Denied(denial); return Task.CompletedTask; }
        ClientSynchronizationCoordinator.Lease? lease = null;
        try
        {
            bool acquired = kind == ClientSessionStartKind.Map ? synchronization.TryAcquireSnapshot(out lease) : synchronization.TryBeginManual(out lease);
            if (!acquired) { Denied(kind == ClientSessionStartKind.Map ? "ClashOfRim.SnapshotUpload.Busy" : "ClashOfRim.Sync.StatusBusy"); return Task.CompletedTask; }
            if (!SourceCurrent() || lease?.IsCurrent != true) { lease?.Dispose(); return Task.CompletedTask; }
            var job = new Job(kind, source, context, bound, lease, login.Create(context)); active = job;
            return Run(job);
        }
        catch { lease?.Dispose(); throw; }
        void Denied(string key)
        {
            string text = view.Text(key);
            if (SourceCurrent()) view.LoginStatus(text);
            if (key == "ClashOfRim.SnapshotUpload.Busy" && SourceCurrent()) view.SnapshotStatus(text);
        }
    }
    private bool Current(Job job)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!ReferenceEquals(active, job) || !job.Runtime.IsCurrent || (!job.Released && !job.Admission.IsCurrent)) return false;
        if (job.Accepted is { } accepted) { if (!publication.IsCurrent(accepted)) return false; }
        else
        {
            if (!publication.IsCurrent(job.Source)) return false;
            var context = session.ReadContext();
            if (context.OfflinePassword != job.Context.OfflinePassword || context.SteamAuthTicket != job.Context.SteamAuthTicket) return false;
        }
        return job.Kind != ClientSessionStartKind.Map || session.ReadContext().CurrentSnapshotId == job.ExpectedSnapshot
            || (job.PublishingSnapshot is not null && session.ReadContext().CurrentSnapshotId == job.PublishingSnapshot);
    }
    private async Task Run(Job job)
    {
        bool map = job.Kind == ClientSessionStartKind.Map, sending = false;
        string prefix = map ? "ClashOfRim.MapSession." : "ClashOfRim.Login.";
        bool Step(Action action)
        {
            using var p = ClientSessionWork.EnterPublication();
            if (!Current(job)) return false;
            action(); return Current(job);
        }
        void Notice(string key, bool snapshot = false, params (string Name, object? Value)[] args)
        {
            string text = "";
            if (Step(() => text = view.Text(key, args))) Step(() => { if (snapshot) view.SnapshotStatus(text); else view.LoginStatus(text); });
        }
        bool Ready() => Current(job) && (!map || job.Runtime.IsPlaying && job.Runtime.HasMap) && !mutations.IsPending && Current(job);
        async Task<bool> Publish(Action action)
        {
            bool consumed = false, applied = false;
            await ClientGamePublication.EnqueueAsync((run, discard) => runtime.Enqueue(run, () => { consumed = true; discard(); }),
                job.Source.Scope.Owner.Token, Step, () =>
                {
                    if (consumed) return; consumed = true;
                    if (!Ready()) return;
                    action(); applied = true;
                }).ConfigureAwait(false);
            return applied && Current(job);
        }
        bool Notify(ClientSessionFollowup effect) => Step(() => _ = followups.Dispatch(effect,
            map ? ClientSessionFollowupSource.MapLogin : ClientSessionFollowupSource.ManualLogin, Step));
        try
        {
            if (map)
            {
                Notice(prefix + "StatusUploadingInitialSnapshot");
                Notice(prefix + "StatusPackagingCurrentMap", snapshot: true);
            }
            Notice("ClashOfRim.Login.StatusLoggingIn");
            if (!Ready()) return;
            sending = true;
            await ClientSessionWork.Run(async () =>
            {
                if (!Current(job)) return;
                var result = await job.Login.RequestAsync(map ? "map-session" : "manual-sync", job.Source.Scope.Owner.Token).ConfigureAwait(false);
                if (!Current(job)) return;
                if (!result.Success || result.Response is null)
                {
                    await Publish(() =>
                    {
                        Notice(prefix + (map ? "StatusLoginFailed" : "StatusFailed"), args: new[] { ("CODE", (object?)(result.ErrorCode ?? "")), ("MESSAGE", (object?)(result.Message ?? "")) });
                        if (ClientCompatibilityWarning.FromFailure(result) is { } warning)
                            Step(() => view.CompatibilityMismatch(warning));
                    }); return;
                }
                if (job.Login.Credential is not { } credential)
                {
                    var rejected = ClientProtocolJson.Copy(result.Response);
                    await Publish(() =>
                    {
                        Notice(prefix + (map ? "StatusLoginRejected" : "StatusRejected"), args: new[] { ("CODE", (object?)(rejected.Result?.ErrorCode ?? 0)), ("MESSAGE", (object?)(rejected.Result?.Message ?? "")) });
                        Step(() => view.CompatibilityMismatch(rejected));
                    }); return;
                }
                if (!await Publish(() =>
                {
                    try { job.Login.Publish(job.Source.Scope.Owner); }
                    finally
                    {
                        if (job.Login.Publication is { } proof) job.Accepted = publication.Adopt(job.Source, proof, credential.Receipt);
                    }
                }).ConfigureAwait(false) || job.Accepted is null) return;
                string? snapshot = null;
                if (map)
                {
                    var upload = await snapshots.UploadAsync(false, null, ModSnapshotUploadKinds.MapSessionSynchronization, null,
                        job.Admission, job.Source.Scope.Owner.Token, new(() => Current(job), id => job.PublishingSnapshot = id)).ConfigureAwait(false);
                    if (!Current(job)) return;
                    if (upload.Failure is { } failure)
                    {
                        await Publish(() =>
                        {
                            string detail = "";
                            if (Step(() => detail = view.SnapshotFailure(failure))) Notice(prefix + "StatusUploadFailed", snapshot: true, args: new[] { ("CODE", (object?)failure.Code), ("MESSAGE", (object?)detail) });
                            Notice(prefix + "StatusUploadAborted");
                        }); return;
                    }
                    snapshot = upload.AcceptedSnapshotId;
                    if (snapshot != session.ReadContext().CurrentSnapshotId) return;
                    job.ExpectedSnapshot = snapshot;
                }
                await Publish(() =>
                {
                    if (map) Notice(prefix + "StatusUploadSucceeded", snapshot: true, args: new[] { ("SNAPSHOT", (object?)snapshot) });
                    Notice(prefix + "StatusSucceeded", args: new[] { ("SNAPSHOT", (object?)snapshot), ("PROTOCOL", (object?)credential.Receipt.ServerProtocolVersion), ("SESSION", (object?)credential.Receipt.SessionId) });
                    if (map)
                    {
                        string message = "";
                        if (!Step(() => message = view.Text("ClashOfRim.ServerSessionConnectedMessage", ("SNAPSHOT", snapshot), ("SESSION", credential.Receipt.SessionId)))
                            || !Step(() => view.Message(message))) return;
                    }
                    if (!Step(() => followups.AdoptQueue(credential.Receipt.EventQueue is null ? null : ClientProtocolJson.Copy(credential.Receipt.EventQueue)))) return;
                    if (!Step(() => publication.Publish(job.Accepted!, map ? "map-session-login" : "manual-login"))) return;
                    job.Released = true; job.Admission.Dispose();
                    foreach (var effect in new[] { ClientSessionFollowup.Events, ClientSessionFollowup.Players }) if (!Notify(effect)) return;
                    if (!map) return;
                    foreach (var effect in new[] { ClientSessionFollowup.Chat, ClientSessionFollowup.ColonySites }) if (!Notify(effect)) return;
                    if (!runtime.IsPresenceRunning) Notify(ClientSessionFollowup.Presence);
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!Current(job)) return;
            void Failed()
            {
                Notice(prefix + "StatusException", args: new[] { ("TYPE", (object?)ex.GetType().Name), ("MESSAGE", (object?)ex.Message) });
                Step(() => view.Diagnose("Session startup failed: " + ex));
            }
            if (sending) await Publish(Failed).ConfigureAwait(false); else Failed();
        }
        finally
        {
            try { job.Admission.Dispose(); }
            finally
            {
                using (ClientSessionWork.EnterPublication()) if (ReferenceEquals(active, job)) active = null;
                await job.Login.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
    private sealed class Job
    {
        internal readonly ClientSessionStartKind Kind;
        internal readonly ClientWorldMarkers.Observation Source;
        internal readonly ClashOfRimClientNetworkContext Context;
        internal readonly IClientSessionStartupSession Runtime;
        internal readonly ClientSynchronizationCoordinator.Lease Admission;
        internal readonly ClientSessionLogin.Attempt Login;
        internal ClientWorldLoginPublication.Accepted? Accepted;
        internal string? ExpectedSnapshot, PublishingSnapshot;
        internal bool Released;
        internal Job(ClientSessionStartKind kind, ClientWorldMarkers.Observation source, ClashOfRimClientNetworkContext context, IClientSessionStartupSession runtime,
            ClientSynchronizationCoordinator.Lease admission, ClientSessionLogin.Attempt login)
        { Kind = kind; Source = source; Context = context; Runtime = runtime; Admission = admission; Login = login; ExpectedSnapshot = context.CurrentSnapshotId; }
    }
}
