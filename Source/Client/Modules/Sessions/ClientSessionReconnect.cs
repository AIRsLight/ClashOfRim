using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

internal sealed class ClientSessionReconnect
{
    private readonly IClientSessionReconnectTransport transport;
    private readonly ClientSessionLifecycle lifecycle;
    private readonly ClientSynchronizationCoordinator synchronization;
    private Attempt? current;

    internal ClientSessionReconnect(IClientSessionReconnectTransport transport, ClientSessionLifecycle lifecycle, ClientSynchronizationCoordinator synchronization)
    { this.transport = transport; this.lifecycle = lifecycle; this.synchronization = synchronization; }

    internal bool IsRunning => Volatile.Read(ref current) is Attempt attempt && lifecycle.Owns(attempt.Owner);

    internal SessionReconnectBlockReason TryBegin(SessionReconnectLocalState local, out Attempt? attempt)
    {
        using var publication = ClientSessionWork.EnterPublication();
        attempt = null;
        var context = lifecycle.ReadContext();
        var blocked = SessionReconnectSafetyPolicy.EvaluateLocalState(local.IsPlaying, local.HasGame, context.IsConfigured,
            context.UserId, context.ColonyId, context.CurrentSnapshotId, IsRunning, local.AtomicMutationPending);
        if (blocked != SessionReconnectBlockReason.None) return blocked;
        if (!synchronization.TryBeginManual(out var admission)) return SessionReconnectBlockReason.SynchronizationBusy;
        attempt = new Attempt(this, context, lifecycle.CaptureOwnership(), admission);
        Volatile.Write(ref current, attempt);
        return SessionReconnectBlockReason.None;
    }

    // A prepared login remains owned by this attempt until the main thread adopts it.
    // Dispose after Prepare/Complete, including when the queued engine action is discarded.
    internal sealed class Attempt : IAsyncDisposable
    {
        private readonly ClientSessionReconnect service;
        private readonly object completionGate = new();
        private readonly ClientSynchronizationCoordinator.Lease admission;
        private ModLoginResponseDto? login;
        private ClashOfRimClientNetworkContext? authenticated;
        private SessionReconnectResult? prepared;
        private bool adopted, disposed;

        internal Attempt(ClientSessionReconnect service, ClashOfRimClientNetworkContext expected, ClientSessionLifecycle.Ownership owner,
            ClientSynchronizationCoordinator.Lease admission)
        { this.service = service; Expected = expected; Owner = owner; this.admission = admission; }
        internal ClashOfRimClientNetworkContext Expected { get; }
        internal ClientSessionLifecycle.Ownership Owner { get; }
        private bool IsCurrent => ReferenceEquals(Volatile.Read(ref service.current), this) && service.lifecycle.Owns(Owner);

        internal async Task PrepareAsync()
        {
            try { prepared = await PrepareCore(); }
            catch (Exception ex) { prepared = new(IsCurrent ? SessionReconnectOutcome.NetworkFailed : SessionReconnectOutcome.Obsolete, ex.Message); }
        }

        private async Task<SessionReconnectResult> PrepareCore()
        {
            if (!IsCurrent) return new(SessionReconnectOutcome.Obsolete);
            var preparation = await service.transport.PrepareAsync(Expected, Owner.Token);
            if (!IsCurrent) return new(SessionReconnectOutcome.Obsolete);
            if (!preparation.Success || preparation.Response is null)
                return new(SessionReconnectOutcome.NetworkFailed, preparation.Message ?? preparation.ErrorCode);
            var world = preparation.Response;
            if (world.Result?.Accepted != true) return new(SessionReconnectOutcome.Rejected, world.Result?.Message);
            if (!SessionReconnectSafetyPolicy.MatchesAuthoritativeSnapshot(Expected.ColonyId, Expected.CurrentSnapshotId!,
                world.HasExistingColony, world.AssignedColonyId, world.LatestSnapshotId))
                return new(SessionReconnectOutcome.SnapshotChanged, "Before login: expectedColony=" + Expected.ColonyId
                    + ", assignedColony=" + world.AssignedColonyId + ", expectedSnapshot=" + Expected.CurrentSnapshotId + ", latestSnapshot=" + world.LatestSnapshotId);

            // Capture a returned credential BEFORE checking ownership; even a late accepted
            // login must be released with its own authenticated identity.
            var response = await service.transport.LoginAsync(Expected, Owner.Token);
            if (response.Success && response.Response is { } receipt && ClientSessionCredential.Capture(Expected, receipt) is { } credential)
            {
                login = credential.Receipt;
                authenticated = credential.Context;
            }
            if (!IsCurrent) return new(SessionReconnectOutcome.Obsolete);
            if (!response.Success || response.Response is null)
                return new(SessionReconnectOutcome.NetworkFailed, response.Message ?? response.ErrorCode);
            if (authenticated is null) return new(SessionReconnectOutcome.Rejected, response.Response.Result?.Message);
            if (!string.Equals(authenticated.UserId, Expected.UserId, StringComparison.Ordinal))
                return new(SessionReconnectOutcome.IdentityChanged);

            var metadata = await service.transport.ReadSnapshotAsync(authenticated, Owner.Token);
            if (!IsCurrent) return new(SessionReconnectOutcome.Obsolete);
            if (!metadata.Success || metadata.Response?.Result?.Accepted != true
                || !string.Equals(metadata.Response.SnapshotId, Expected.CurrentSnapshotId, StringComparison.Ordinal))
                return new(SessionReconnectOutcome.SnapshotChanged, "After login: expectedSnapshot=" + Expected.CurrentSnapshotId
                    + ", latestSnapshot=" + metadata.Response?.SnapshotId + ", error=" + (metadata.Message ?? metadata.Response?.Result?.Message ?? metadata.ErrorCode));
            return new(SessionReconnectOutcome.Succeeded, Login: login, Snapshot: metadata.Response);
        }

        internal SessionReconnectResult Complete(bool isPlaying, bool hasOriginalGame, bool atomicMutationPending)
        {
            try { return CompleteCore(isPlaying, hasOriginalGame, atomicMutationPending); }
            finally { admission.Dispose(); }
        }

        private SessionReconnectResult CompleteCore(bool isPlaying, bool hasOriginalGame, bool atomicMutationPending)
        {
            using var publication = ClientSessionWork.EnterPublication();
            lock (completionGate)
            {
                if (disposed || adopted || !IsCurrent) return new(SessionReconnectOutcome.Obsolete);
                var result = prepared ?? throw new InvalidOperationException("Reconnect must be prepared before completion.");
                if (result.Outcome != SessionReconnectOutcome.Succeeded) return result;
                var currentContext = service.lifecycle.ReadContext();
                var blocked = SessionReconnectSafetyPolicy.EvaluateLocalState(isPlaying, hasOriginalGame, currentContext.IsConfigured,
                    currentContext.UserId, currentContext.ColonyId, currentContext.CurrentSnapshotId, synchronizationBusy: false, atomicMutationPending);
                if (blocked != SessionReconnectBlockReason.None) return new(SessionReconnectOutcome.LocalStateBlocked, BlockReason: blocked);
                if (!string.Equals(currentContext.ServerBaseUrl, Expected.ServerBaseUrl, StringComparison.Ordinal)
                    || !string.Equals(currentContext.UserId, Expected.UserId, StringComparison.Ordinal)
                    || !string.Equals(currentContext.ColonyId, Expected.ColonyId, StringComparison.Ordinal))
                    return new(SessionReconnectOutcome.IdentityChanged);
                if (!string.Equals(currentContext.CurrentSnapshotId, Expected.CurrentSnapshotId, StringComparison.Ordinal))
                    return new(SessionReconnectOutcome.SnapshotChanged);
                var accepted = service.lifecycle.PublishLogin(Owner, Expected, login!);
                adopted = accepted is not null;
                return accepted is not null && service.lifecycle.Owns(accepted)
                    ? result with { Publication = accepted } : new(SessionReconnectOutcome.Obsolete);
            }
        }

        public async ValueTask DisposeAsync()
        {
            ClashOfRimClientNetworkContext? release;
            string? releaseSession;
            lock (completionGate)
            {
                if (disposed) return;
                disposed = true;
                release = adopted ? null : authenticated;
                releaseSession = login?.SessionId;
            }
            try
            {
                admission.Dispose();
                if (release is not null) await service.lifecycle.ReleaseSessionAsync(release, releaseSession!);
            }
            finally { Interlocked.CompareExchange(ref service.current, null, this); }
        }
    }
}

internal sealed record SessionReconnectLocalState(bool IsPlaying, bool HasGame, bool AtomicMutationPending);
internal enum SessionReconnectOutcome { Succeeded, NetworkFailed, Rejected, SnapshotChanged, IdentityChanged, LocalStateBlocked, Obsolete }
internal sealed record SessionReconnectResult(SessionReconnectOutcome Outcome, string? Message = null,
    SessionReconnectBlockReason BlockReason = SessionReconnectBlockReason.None, ModLoginResponseDto? Login = null, ModDownloadLatestSnapshotResponseDto? Snapshot = null,
    ClientSessionLifecycle.Login? Publication = null);
