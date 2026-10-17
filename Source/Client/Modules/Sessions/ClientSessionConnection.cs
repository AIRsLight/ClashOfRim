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

    internal ClientSessionConnection(IClientSessionStreamTransport transport) => this.transport = transport;
    internal bool IsRunning { get { lock (connectionGate) return current is not null; } }
    internal event Action<SessionConnectionStatus>? StatusChanged;
    internal event Action<ModSessionStreamEvent, string>? EventReceived;
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

            var connection = new Connection(sessionId!);
            lock (connectionGate) { previous = current; current = connection; }
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
            lock (connectionGate) { previous = current; current = null; }
            if (reportStatus) Report(new(SessionConnectionPhase.Disconnecting, previous?.SessionId));
        }
        previous?.Cancel();
    }

    private async Task Run(Connection connection, ClashOfRimClientNetworkContext context, long notificationVersion, long worldVersion)
    {
        try
        {
            var result = await ClientSessionWork.Observe(transport.StreamAsync(context, connection.SessionId, notificationVersion, worldVersion,
                item => Publish(connection, () => EventReceived?.Invoke(item, connection.SessionId)), connection.Token));
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
                    ConnectionLost?.Invoke(connection.SessionId);
                    return;
                }
                Report(new(result.Response.Cancelled ? SessionConnectionPhase.Cancelled : SessionConnectionPhase.Closed, connection.SessionId));
                if (!result.Response.Cancelled) ConnectionLost?.Invoke(connection.SessionId);
            });
        }
        catch (Exception ex) when (ClientSessionWork.IsCurrent)
        {
            Publish(connection, () =>
            {
                Report(new(SessionConnectionPhase.Exception, connection.SessionId, Error: ex));
                if (!connection.Token.IsCancellationRequested) ConnectionLost?.Invoke(connection.SessionId);
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

    private void Publish(Connection connection, Action action)
    {
        ClientSessionWork.TryApply(() =>
        {
            if (connection.OwnerToken != ClientSessionWork.Token || connection.OwnerToken.IsCancellationRequested) return;
            lock (connectionGate) { if (!ReferenceEquals(current, connection)) return; }
            // Start/Stop also require session publication, so ownership cannot change during this callback.
            action();
        });
    }

    private void Report(SessionConnectionStatus status) => StatusChanged?.Invoke(status);

    private sealed class Connection : IDisposable
    {
        private readonly object lifetimeGate = new();
        private readonly CancellationTokenSource cancellation = new();
        private bool disposed;
        internal Connection(string sessionId) { SessionId = sessionId; Token = cancellation.Token; OwnerToken = ClientSessionWork.Token; }
        internal string SessionId { get; }
        internal CancellationToken Token { get; }
        internal CancellationToken OwnerToken { get; }
        // Stop and an I/O completion can race after leaving the publication scope.
        internal void Cancel() { lock (lifetimeGate) { if (!disposed) cancellation.Cancel(); } }
        public void Dispose() { lock (lifetimeGate) { if (!disposed) { disposed = true; cancellation.Dispose(); } } }
    }
}

internal enum SessionConnectionPhase { NotConfigured, SessionMissing, Connecting, Disconnecting, Cancelled, Failed, Closed, Exception }
internal sealed record SessionConnectionStatus(SessionConnectionPhase Phase, string? SessionId, string? ErrorCode = null, string? Message = null, Exception? Error = null);
