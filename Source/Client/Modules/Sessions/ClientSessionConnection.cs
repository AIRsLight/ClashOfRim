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

    internal ClientSessionConnection(IClientSessionStreamTransport transport) => this.transport = transport;
    internal bool IsRunning { get { lock (connectionGate) return current is not null; } }
    internal event Action<SessionConnectionStatus>? StatusChanged;
    internal event Action<ModSessionStreamEvent, Observation>? EventReceived;
    internal event Action<string>? ConnectionLost;

    internal Task Start(ClashOfRimClientNetworkContext context, string? sessionId, long notificationVersion, long worldVersion)
    {
        Connection? previous;
        Task work;
        using (ClientSessionWork.EnterPublication())
        {
            if (!context.IsConfigured)
            {
                Report(new(SessionConnectionPhase.NotConfigured, sessionId));
                return Task.CompletedTask;
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Report(new(SessionConnectionPhase.SessionMissing, sessionId));
                return Task.CompletedTask;
            }

            var connection = new Connection(this, context, sessionId!);
            lock (connectionGate) { previous = current; revision = current = connection; }
            // Preserve cancellation-before-open ordering; the server admits only one active stream.
            previous?.Cancel();
            Report(new(SessionConnectionPhase.Connecting, sessionId));
            // Capture the same session generation as the publication above.
            work = Finish(ClientSessionWork.Run(() => Run(connection, context, notificationVersion, worldVersion)), connection);
        }
        return work;
    }

    internal void Stop(bool reportStatus = true)
    {
        Connection? previous;
        using (ClientSessionWork.EnterPublication())
        {
            lock (connectionGate) { previous = current; current = null; revision = new(); }
            if (reportStatus) Report(new(SessionConnectionPhase.Disconnecting, previous?.SessionId));
        }
        previous?.Cancel();
    }

    private async Task Run(Connection connection, ClashOfRimClientNetworkContext context, long notificationVersion, long worldVersion)
    {
        try
        {
            if (!Publish(connection, static () => { })) return;
            var result = await ClientSessionWork.Observe(transport.StreamAsync(context, connection.SessionId, notificationVersion, worldVersion,
                item =>
                {
                    foreach (Action<ModSessionStreamEvent, Observation> handler in EventReceived?.GetInvocationList() ?? Array.Empty<Delegate>())
                        if (!Publish(connection, () => handler(item, connection.Receipt))) break;
                }, connection.Token));
            Publish(connection, () =>
            {
                if (connection.Token.IsCancellationRequested)
                {
                    Report(new(SessionConnectionPhase.Cancelled, connection.SessionId));
                    return;
                }
                if (!result.Success || result.Response is null)
                {
                    Report(new(SessionConnectionPhase.Failed, connection.SessionId, result.ErrorCode, result.Message));
                    Publish(connection, () => ConnectionLost?.Invoke(connection.SessionId));
                    return;
                }
                Report(new(result.Response.Cancelled ? SessionConnectionPhase.Cancelled : SessionConnectionPhase.Closed, connection.SessionId));
                if (!result.Response.Cancelled) Publish(connection, () => ConnectionLost?.Invoke(connection.SessionId));
            });
        }
        catch (Exception ex) when (ClientSessionWork.IsCurrent)
        {
            Publish(connection, () =>
            {
                Report(new(SessionConnectionPhase.Exception, connection.SessionId, Error: ex));
                if (!connection.Token.IsCancellationRequested) Publish(connection, () => ConnectionLost?.Invoke(connection.SessionId));
            });
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
        private readonly ClientSessionConnection owner;
        private readonly object revision;
        private readonly CancellationToken generation;
        internal ClashOfRimClientNetworkContext Context { get; }
        internal string SessionId { get; }
        internal Observation(ClientSessionConnection owner, object revision, ClashOfRimClientNetworkContext context, string id, CancellationToken generation)
        { this.owner = owner; this.revision = revision; Context = context; SessionId = id; this.generation = generation; }
        internal bool TryApply(Action action)
        {
            bool applied = false;
            ClientSessionWork.TryApply(() =>
            {
                if (generation != ClientSessionWork.Token || generation.IsCancellationRequested) return;
                lock (owner.connectionGate) if (!ReferenceEquals(revision, owner.revision)) return;
                action(); applied = true;
            });
            return applied;
        }
    }

    private void Report(SessionConnectionStatus status) => StatusChanged?.Invoke(status);

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
internal sealed record SessionConnectionStatus(SessionConnectionPhase Phase, string? SessionId, string? ErrorCode = null, string? Message = null, Exception? Error = null);
