using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal sealed class ClientSnapshotReload : IClientSnapshotReload
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly ClientSnapshotFailureRecovery recovery;
    private readonly IClientSessionAuthentication authentication;
    private readonly ClientColonySnapshotDownload download;
    private readonly ClientSnapshotPublication publication;
    private readonly IClientSnapshotReloadRuntime runtime;
    private readonly IClientSnapshotReloadPresentation presentation;
    private Job? active;
    internal ClientSnapshotReload(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientSnapshotFailureRecovery recovery, IClientSessionAuthentication authentication,
        ClientColonySnapshotDownload download, ClientSnapshotPublication publication, IClientSnapshotReloadRuntime runtime,
        IClientSnapshotReloadPresentation presentation)
    { this.session = session; this.synchronization = synchronization; this.mutations = mutations; this.recovery = recovery;
        this.authentication = authentication; this.download = download; this.publication = publication; this.runtime = runtime; this.presentation = presentation; }
    internal event Action<ClientSnapshotReloadCompleted>? Completed;
    internal bool BlocksGameLifecycle => mutations.BlocksGameLifecycle;

    // A replaced game may never dispatch the queued vanilla load callback. Engine
    // updates/new requests retire that exact operation instead of retaining its lease.
    internal void Advance()
    {
        using var p = ClientSessionWork.EnterPublication();
        if (active is not { } job || Owns(job, requireAdmission: job.Admission is not null) || job.Failed) return;
        active = null;
        // Network cancellation callbacks must not run synchronously under publication.
        lock (job)
            if (!job.CancellationReleased) job.CancellationCompletion = job.Cancellation.CancelAsync();
        job.Load.Dispose(); job.Admission?.Dispose();
        if (job.Mutation is not null && ReferenceEquals(mutations.Current, job.Mutation)) mutations.Reset();
        presentation.Close(job);
    }

    public Task StartAsync(ClientSnapshotReloadRequest request, ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        Advance();
        if (active is { } running && Owns(running, requireAdmission: false)) return running.Work;
        var blocked = mutations.Current;
        if (blocked is not null && (request.Purpose != ClientSnapshotReloadPurpose.Raid
            || blocked.Kind is not (LocalMutationKind.Raid or LocalMutationKind.RaidAttacker or LocalMutationKind.RaidRestore)))
        { presentation.Failure(new object(), presentation.Text("ClashOfRim.Sync.StatusBusy"), request.Purpose); return Task.CompletedTask; }
        if (string.IsNullOrWhiteSpace(request.ExpectedSnapshotId))
        {
            string message = presentation.Text("ClashOfRim.ExistingColonyMissingSnapshot");
            presentation.Failure(new object(), message, request.Purpose);
            if (request.Purpose == ClientSnapshotReloadPurpose.Raid)
                recovery.Show(LocalMutationKind.RaidRestore, message, static () => { });
            return Task.CompletedTask;
        }
        var frozen = request with { WorldConfiguration = request.WorldConfiguration is null ? null
            : ClientProtocolJson.Deserialize<ModWorldConfigurationDto>(ClientProtocolJson.Serialize(request.WorldConfiguration), t => t) };
        var job = new Job(frozen, session.ReadContext(), session.CaptureOwnership(), runtime.Bind());
        return Begin(job, continuation);
    }
    private Task Begin(Job job, ClientSynchronizationCoordinator.Lease? continuation)
    {
        if (job.Request.Purpose == ClientSnapshotReloadPurpose.Entry
            && !synchronization.TryBeginManual(out job.Admission, continuation))
        { job.Load.Dispose(); job.Cancellation.Dispose(); presentation.Failure(job, presentation.Text("ClashOfRim.Sync.StatusBusy"), job.Request.Purpose); return Task.CompletedTask; }
        try
        {
            // Transfer synchronously, before the entry caller can finalize its own lease.
            if (job.Admission is { } lease) job.Admission = lease.TransferManual();
            active = job;
            recovery.Close();
            mutations.Begin(job.Kind, presentation.Text("ClashOfRim.ServerEntry.StageLoad")); job.Mutation = mutations.Current;
            presentation.Progress(job, job.Admission is null ? ClientSnapshotReloadStage.Waiting : ClientSnapshotReloadStage.Downloading, job.Request.ExpectedSnapshotId!);
            return job.Work = RunOwned(job);
        }
        catch
        {
            job.Admission?.Dispose(); job.Load.Dispose(); job.Cancellation.Dispose();
            if (ReferenceEquals(active, job)) active = null;
            if (job.Mutation is not null && ReferenceEquals(mutations.Current, job.Mutation)) mutations.Reset();
            throw;
        }
    }
    private async Task RunOwned(Job job)
    {
        try { await ClientSessionWork.Run(() => RunAsync(job)).ConfigureAwait(false); }
        finally
        {
            job.Admission?.Dispose(); job.Load.Dispose();
            Task cancellation;
            lock (job) { job.CancellationReleased = true; cancellation = job.CancellationCompletion; }
            await cancellation.ConfigureAwait(false); job.Cancellation.Dispose();
            ClientSessionWork.TryApply(() =>
            {
                if (ReferenceEquals(active, job)) active = null;
                if (!job.Failed && job.Mutation is not null && ReferenceEquals(mutations.Current, job.Mutation)) mutations.Reset();
            });
            // Release local admission before waiting for logout, with the credential actually received.
            if (!job.Adopted && job.Credential is { } unused)
                await session.ReleaseSessionAsync(unused.Context, unused.Receipt.SessionId!).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Job job)
    {
        try
        {
            if (job.Admission is null) job.Admission = await synchronization.AcquireManualAsync(job.Token).ConfigureAwait(false);
            if (!Apply(job, static () => { })) return;
            if (string.IsNullOrWhiteSpace(session.SessionId) || string.IsNullOrWhiteSpace(job.Context.AuthToken))
            {
                await Publish(job, () => presentation.Progress(job, ClientSnapshotReloadStage.Login, "")).ConfigureAwait(false);
                if (!Apply(job, static () => { })) return;
                var login = await authentication.LoginAsync(job.Context, "restore-existing-snapshot", null, job.Token,
                    receipt => job.Credential = ClientSessionCredential.Capture(job.Context, receipt)).ConfigureAwait(false);
                await Publish(job, () =>
                {
                    if (!login.Success || job.Credential is not { } credential)
                    { Fail(job, presentation.LoginFailure(login), !login.Success && ClientNetworkFailurePolicy.CanRetry(login.ErrorCode, login.Message)); return; }
                    if (credential.Context.UserId != job.Context.UserId)
                    { Fail(job, presentation.Text("ClashOfRim.SnapshotReload.IdentityChanged"), false); return; }
                    session.PublishLogin(credential.Receipt); job.Adopted = true;
                    job.Owner = session.CaptureOwnership(); job.Context = session.ReadContext();
                }).ConfigureAwait(false);
                if (!job.Adopted) return;
            }
            if (job.Snapshot is null)
            {
                await Publish(job, () => presentation.Progress(job, ClientSnapshotReloadStage.Metadata, "")).ConfigureAwait(false);
                if (!Apply(job, static () => { })) return;
                var result = await download.DownloadAsync(job.Context, job.Token, id =>
                    runtime.Enqueue(() => Apply(job, () => presentation.Progress(job, ClientSnapshotReloadStage.Payload, id)), static () => { })).ConfigureAwait(false);
                await Publish(job, () =>
                {
                    if (result.Snapshot is not { } snapshot)
                    { var f = result.Failure!; Fail(job, presentation.DownloadFailure(f),
                        f.Kind is ClientColonySnapshotFailureKind.Metadata or ClientColonySnapshotFailureKind.Payload && ClientNetworkFailurePolicy.CanRetry(f.Code, f.Message)); return; }
                    job.Snapshot = snapshot;
                }).ConfigureAwait(false);
                if (job.Snapshot is null) return;
            }
            Task<bool>? loading = null;
            await Publish(job, () =>
            {
                presentation.Progress(job, ClientSnapshotReloadStage.Loading, job.Snapshot.SnapshotId);
                presentation.Close(job);
                loading = job.Load.LoadAsync(job.Snapshot, action => Apply(job, action), job.Token);
            }).ConfigureAwait(false);
            if (loading is null) return;
            bool loaded = await loading.ConfigureAwait(false);
            await Publish(job, () =>
            {
                if (!loaded) { Fail(job, presentation.Text("ClashOfRim.SnapshotReload.LoadDiscarded"), true); return; }
                job.Publishing = true;
                publication.PublishRestoredGame(job.Load.Game, job.Snapshot.SnapshotId, job.Snapshot.LineageToken);
                // Only this publication may rebase the captured context.
                if (!Owns(job, requireSnapshot: false) || session.ReadContext().CurrentSnapshotId != job.Snapshot.SnapshotId) return;
                job.Context = WithSnapshot(job.Context, job.Snapshot.SnapshotId);
                mutations.Reset(); job.Mutation = null;
                recovery.Close();
                presentation.Progress(job, ClientSnapshotReloadStage.Loaded, job.Snapshot.SnapshotId);
                job.Admission!.Dispose(); active = null;
                Completed?.Invoke(new(job.Snapshot, job.Request));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Publish(job, () => Fail(job, presentation.Exception(ex), ClientNetworkFailurePolicy.CanRetry(ex)),
                requireSnapshot: !job.Publishing).ConfigureAwait(false);
        }
    }
    private void Fail(Job job, string message, bool canRetry)
    {
        if (job.Publishing && session.ReadContext().CurrentSnapshotId != job.Context.CurrentSnapshotId
            && session.ReadContext().CurrentSnapshotId != job.Snapshot?.SnapshotId) return;
        job.Failed = true;
        presentation.Failure(job, message, job.Request.Purpose);
        if (job.Request.Purpose == ClientSnapshotReloadPurpose.Entry && !job.Load.MutationStarted)
        { if (ReferenceEquals(mutations.Current, job.Mutation)) mutations.Reset(); return; }
        recovery.Show(job.Kind, message, () => _ = Retry(job), canRetry && !job.Load.MutationStarted && !job.Publishing);
    }
    private Task Retry(Job original)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!original.Load.IsCurrent || !session.Owns(original.Owner, original.Context, false)) return Task.CompletedTask;
        if (!session.Owns(original.Owner, original.Context, true))
        { recovery.Show(original.Kind, presentation.Text("ClashOfRim.SnapshotConfirmation.BaseSnapshotChanged"), () => { }); return Task.CompletedTask; }
        return Begin(new(original.Request, original.Context, original.Owner, runtime.Bind()) { Snapshot = original.Snapshot }, null);
    }
    private Task Publish(Job job, Action action, bool requireSnapshot = true) => ClientGamePublication.EnqueueAsync(runtime.Enqueue,
        job.Token, run => Apply(job, run, requireSnapshot), action);
    private bool Apply(Job job, Action action, bool requireSnapshot = true)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() => { if (Owns(job, requireSnapshot)) { action(); applied = true; } });
        return applied;
    }
    private bool Owns(Job job, bool requireSnapshot = true, bool requireAdmission = true) => ReferenceEquals(active, job)
        && job.Load.IsCurrent && session.Owns(job.Owner, job.Context, requireSnapshot)
        && (!requireAdmission || job.Admission?.IsCurrent == true) && ReferenceEquals(mutations.Current, job.Mutation);
    private static ClashOfRimClientNetworkContext WithSnapshot(ClashOfRimClientNetworkContext c, string snapshot) =>
        new(c.ServerBaseUrl, c.UserId, c.ColonyId, snapshot, c.SteamAuthTicket, c.OfflinePassword, c.AuthToken);
    private sealed class Job
    {
        internal Job(ClientSnapshotReloadRequest request, ClashOfRimClientNetworkContext context, ClientSessionLifecycle.Ownership owner, IClientSnapshotLoadOperation load)
        { Request = request; Context = context; Owner = owner; Load = load; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Token); Token = Cancellation.Token; }
        internal readonly CancellationTokenSource Cancellation;
        internal readonly CancellationToken Token;
        internal readonly ClientSnapshotReloadRequest Request;
        internal readonly IClientSnapshotLoadOperation Load;
        internal ClashOfRimClientNetworkContext Context;
        internal ClientSessionLifecycle.Ownership Owner;
        internal ClientSynchronizationCoordinator.Lease? Admission;
        internal LocalMutationState? Mutation;
        internal ClientSessionCredential? Credential;
        internal ClientRestoredSnapshot? Snapshot;
        internal bool Adopted, Publishing, Failed, CancellationReleased;
        internal Task Work = Task.CompletedTask, CancellationCompletion = Task.CompletedTask;
        internal LocalMutationKind Kind => Request.Purpose == ClientSnapshotReloadPurpose.Raid ? LocalMutationKind.RaidRestore : LocalMutationKind.SnapshotRestore;
    }
}
