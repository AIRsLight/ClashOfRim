using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

// One socket owns one sender gate and receiver. Session/notification policies remain in their modules.
internal sealed class SessionWebSocketConnection(WebSocket socket, SessionConnections sessions, SessionNotifications notifications) : IAsyncDisposable
{
    private readonly SemaphoreSlim sendGate = new(1, 1);

    internal async Task RunAsync(string userId, string colonyId, string sessionId, OnlinePresenceLease presence,
        long knownLedger, long knownWorld, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cursor = notifications.Capture(userId, knownLedger, knownWorld);
        Task receive = ReceiveAsync(userId, colonyId, sessionId, presence, stop.Token);
        try
        {
            await SendAsync(new("presence", new SessionStreamEventDto(userId, true, cursor.Ledger, ServerLocalization.Text("SessionStream.Connected"))), stop.Token);
            while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                if (sessions.Check(userId, colonyId, sessionId, presence) is { } denied)
                {
                    await ErrorAsync(userId, denied.Message, stop.Token);
                    break;
                }
                using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                Task<SessionNotificationBatch> wait = notifications.WaitAsync(cursor, waitStop.Token);
                try
                {
                    if (await Task.WhenAny(wait, receive) == receive) { await receive; break; }
                    var batch = await wait;
                    cursor = batch.Cursor;
                    // Recheck after the wait and between sends in an online-player batch.
                    foreach (var message in batch.Messages)
                    {
                        if (sessions.Check(userId, colonyId, sessionId, presence) is { } expired)
                        {
                            await ErrorAsync(userId, expired.Message, stop.Token);
                            return;
                        }
                        await SendAsync(message, stop.Token);
                    }
                }
                finally
                {
                    waitStop.Cancel();
                    try { await wait; } catch (OperationCanceledException) when (waitStop.IsCancellationRequested) { }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            try { await receive; }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task ReceiveAsync(string userId, string colonyId, string sessionId, OnlinePresenceLease presence, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            string? message = await ReadTextAsync(cancellationToken);
            if (message is null) return;
            if (EventName(message) != "ping") continue;
            if (sessions.Heartbeat(userId, colonyId, sessionId, presence) is { } denied)
            {
                await ErrorAsync(userId, denied.Message, cancellationToken);
                return;
            }
            await SendAsync(new("pong", new SessionStreamEventDto(userId, true, notifications.LedgerVersion(userId), null)), cancellationToken);
        }
    }

    private async Task<string?> ReadTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType == WebSocketMessageType.Text) stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string? EventName(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("eventName", out var name) || root.TryGetProperty("event", out name) || root.TryGetProperty("type", out name))
                return name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (JsonException) { }
        return null;
    }

    private Task ErrorAsync(string userId, string? message, CancellationToken token)
        => SendAsync(new("error", new SessionStreamEventDto(userId, false, notifications.LedgerVersion(userId), message)), token);

    private async Task SendAsync(SessionStreamMessage message, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { eventName = message.EventName, data = JsonSerializer.Serialize(message.Data) });
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally { sendGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(SessionConnections.HeartbeatInterval);
                // Send the closing frame without waiting indefinitely for a peer's acknowledgment.
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session stream closed.", close.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
            }
        }
        finally
        {
            try { socket.Dispose(); } finally { sendGate.Dispose(); }
        }
    }
}
