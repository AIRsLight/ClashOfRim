using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

// Owns connection replacement and cleanup, sharing the existing session publication boundary.
internal sealed class ClientSessionConnection
{
    private readonly IClientSessionStreamTransport transport;
    private readonly object connectionGate = new();
    private Connection? current;
    // Completed frames may still have UI work queued. Natural completion keeps
    // their revision; Stop or replacement revokes it even for the same SessionId.
    private object revision = new();
    private object statusRevision = new();

    internal ClientSessionConnection(IClientSessionStreamTransport transport) => this.transport = transport;
    internal bool IsRunning { get { lock (connectionGate) return current is not null; } }
    internal event Action<SessionConnectionStatus, Publication>? StatusChanged;
    internal event Action<ModSessionStreamEvent, Observation>? EventReceived;
    internal event Action<Observation>? ConnectionLost;

    internal Task Start(ClashOfRimClientNetworkContext context, string? sessionId, long notificationVersion, long worldVersion)
    {
        Connection? previous;
        Task work;
        using (ClientSessionWork.EnterPublication())
        {
            if (!context.IsConfigured)
            {
                Report(new(SessionConnectionPhase.NotConfigured, sessionId, Context: context), CapturePublication());
                return Task.CompletedTask;
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Report(new(SessionConnectionPhase.SessionMissing, sessionId, Context: context), CapturePublication());
                return Task.CompletedTask;
            }

            var connection = new Connection(this, context, sessionId!);
            lock (connectionGate) { previous = current; revision = current = connection; }
            try
            {
                // Preserve cancellation-before-open ordering; the server admits only one active stream.
                previous?.Cancel();
                Report(new(SessionConnectionPhase.Connecting, sessionId, Context: context), connection.Receipt.Publication);
                // Capture the same session generation as the publication above.
                work = Finish(ClientSessionWork.Run(() => Run(connection, context, notificationVersion, worldVersion)), connection);
            }
            catch
            {
                // No worker owns this resource until scheduling succeeds. A subscriber
                // can replace the connection before throwing, so detach only our source.
                lock (connectionGate) { if (ReferenceEquals(current, connection)) { current = null; revision = new(); } }
                try { connection.Cancel(); }
                finally { connection.Dispose(); }
                throw;
            }
        }
        return work;
    }

    internal void Stop(bool reportStatus = true)
    {
        using var publication = ClientSessionWork.EnterPublication();
        Connection? previous;
        lock (connectionGate) { previous = current; current = null; revision = new(); }
        var source = CapturePublication();
        // Cancel before observers can reopen a stream, including when they throw.
        previous?.Cancel();
        if (reportStatus) Report(new(SessionConnectionPhase.Disconnecting, previous?.SessionId, Context: previous?.Receipt.Context), source);
    }

    private async Task Run(Connection connection, ClashOfRimClientNetworkContext context, long notificationVersion, long worldVersion)
    {
        SessionConnectionStatus status;
        bool lost;
        try
        {
            if (!Publish(connection, static () => { })) return;
            var result = await ClientSessionWork.Observe(transport.StreamAsync(context, connection.SessionId, notificationVersion, worldVersion,
                item =>
                {
                    foreach (Action<ModSessionStreamEvent, Observation> handler in EventReceived?.GetInvocationList() ?? Array.Empty<Delegate>())
                        if (!Publish(connection, () => handler(item, connection.Receipt))) break;
                }, connection.Token));
            if (connection.Token.IsCancellationRequested)
                status = new(SessionConnectionPhase.Cancelled, connection.SessionId);
            else if (!result.Success || result.Response is null)
                status = new(SessionConnectionPhase.Failed, connection.SessionId, result.ErrorCode, result.Message);
            else status = new(result.Response.Cancelled ? SessionConnectionPhase.Cancelled : SessionConnectionPhase.Closed, connection.SessionId);
            lost = status.Phase != SessionConnectionPhase.Cancelled;
        }
        catch (Exception ex) when (ClientSessionWork.IsCurrent)
        {
            status = new(SessionConnectionPhase.Exception, connection.SessionId, Error: ex);
            lost = !connection.Token.IsCancellationRequested;
        }
        // Subscriber failures are not transport failures. Do not replay terminal
        // notifications when a status/loss observer throws; Finish still cleans up.
        try { Publish(connection, () => Report(status with { Context = context }, connection.Receipt.Publication)); }
        finally
        {
            if (lost)
                foreach (Action<Observation> handler in ConnectionLost?.GetInvocationList() ?? Array.Empty<Delegate>())
                    if (!Publish(connection, () => handler(connection.Receipt))) break;
        }
    }

    private async Task Finish(Task work, Connection connection)
    {
        try { await work; }
        finally
        {
            // Also runs if the session expires before ClientSessionWork starts the delegate.
            lock (connectionGate) { if (ReferenceEquals(current, connection)) current = null; }
            connection.Dispose();
        }
    }

    private bool Publish(Connection connection, Action action)
    {
        bool applied = false;
        connection.Receipt.TryApply(() =>
        {
            lock (connectionGate) { if (!ReferenceEquals(current, connection)) return; }
            action(); applied = true;
        });
        return applied;
    }

    internal sealed class Observation
    {
        internal Publication Publication { get; }
        internal ClashOfRimClientNetworkContext Context { get; }
        internal string SessionId { get; }
        internal Observation(ClientSessionConnection owner, object revision, ClashOfRimClientNetworkContext context, string id, CancellationToken generation)
        { Publication = new(owner, revision, generation); Context = context; SessionId = id; }
        internal bool TryApply(Action action) => Publication.TryApply(action);
    }

    // Shared revision/generation proof for both frames and status/loss observers.
    // Stop has its own proof after revoking the previous stream; it may be idle.
    internal sealed class Publication
    {
        private readonly ClientSessionConnection owner;
        private readonly object revision;
        private readonly CancellationToken generation;
        private readonly object? statusRevision;
        internal Publication(ClientSessionConnection owner, object revision, CancellationToken generation, object? statusRevision = null)
        { this.owner = owner; this.revision = revision; this.generation = generation; this.statusRevision = statusRevision; }
        internal Publication ForStatus()
        {
            lock (owner.connectionGate)
            {
                owner.statusRevision = new();
                return new(owner, revision, generation, owner.statusRevision);
            }
        }
        internal bool TryApply(Action action)
        {
            bool applied = false;
            ClientSessionWork.TryApply(() =>
            {
                if (generation != ClientSessionWork.Token || generation.IsCancellationRequested) return;
                lock (owner.connectionGate)
                    if (!ReferenceEquals(revision, owner.revision)
                        || (statusRevision is not null && !ReferenceEquals(statusRevision, owner.statusRevision))) return;
                action(); applied = true;
            });
            return applied;
        }
    }

    private Publication CapturePublication()
    { lock (connectionGate) return new(this, revision, ClientSessionWork.Token); }

    private void Report(SessionConnectionStatus status, Publication source)
    {
        source.TryApply(() =>
        {
            var receipt = source.ForStatus();
            foreach (Action<SessionConnectionStatus, Publication> handler in StatusChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                if (!receipt.TryApply(() => handler(status, receipt))) break;
        });
    }

    private sealed class Connection : IDisposable
    {
        private readonly object lifetimeGate = new();
        private readonly CancellationTokenSource cancellation = new();
        private bool disposed;
        internal Connection(ClientSessionConnection owner, ClashOfRimClientNetworkContext context, string sessionId)
        { SessionId = sessionId; Token = cancellation.Token; Receipt = new(owner, this, context, sessionId, ClientSessionWork.Token); }
        internal Observation Receipt { get; }
        internal string SessionId { get; }
        internal CancellationToken Token { get; }
        // Stop and an I/O completion can race after leaving the publication scope.
        internal void Cancel() { lock (lifetimeGate) { if (!disposed) cancellation.Cancel(); } }
        public void Dispose() { lock (lifetimeGate) { if (!disposed) { disposed = true; cancellation.Dispose(); } } }
    }
}

internal enum SessionConnectionPhase { NotConfigured, SessionMissing, Connecting, Disconnecting, Cancelled, Failed, Closed, Exception }
internal sealed record SessionConnectionStatus(SessionConnectionPhase Phase, string? SessionId, string? ErrorCode = null, string? Message = null,
    Exception? Error = null, ClashOfRimClientNetworkContext? Context = null);
