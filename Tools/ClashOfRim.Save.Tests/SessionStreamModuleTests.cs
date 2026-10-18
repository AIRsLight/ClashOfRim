using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SessionStreamModuleTests
{
    internal static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(name);
                await Notifications();
                await Exchange();
                await InvalidateOnlineBatch();
                foreach (bool session in new[] { false, true }) await InvalidatedDuringWait(session);
                foreach (bool applicationStop in new[] { false, true }) await CancelWhileWaiting(applicationStop);
                foreach (string failure in new[] { "accept", "receive", "receive-invalid", "send", "close", "close-invalid", "dispose", "recovery", "log-open", "log-close" }) await Cleanup(failure);
                await Presence();
            }
            await BoundedClose();
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static async Task Notifications()
    {
        var f = new Fixture();
        f.S.Events.SignalUser("user"); f.Chat.SignalUser("user");
        f.S.Notifications.SignalPlayerOnline("old", ["user"], DateTimeOffset.UtcNow);
        var cursor = f.Notifications.Capture("user", -1, -1);
        Check(cursor.Ledger == 1 && cursor.Chat == 1 && cursor.Players == 1 && cursor.World == 0, "Capture lost resume/current version rules.");
        f.S.Events.SignalUser("user"); f.Chat.SignalUser("user"); f.World.SignalUser("user");
        f.S.Notifications.SignalPlayerOnline("new", ["user"], DateTimeOffset.UtcNow);
        var names = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var batch = await f.Notifications.WaitAsync(cursor, default).WaitAsync(TimeSpan.FromSeconds(2));
            cursor = batch.Cursor; names.AddRange(batch.Messages.Select(m => m.EventName));
        }
        Check(names.SequenceEqual(new[] { "ledgerChanged", "chatChanged", "worldConfigurationChanged", "playerOnline" })
            && cursor.Ledger == 2 && cursor.Chat == 2 && cursor.World == 1 && cursor.Players == 2, "Simultaneous channels lost or reused another cursor.");
        using var stop = new CancellationTokenSource();
        var wait = f.Notifications.WaitAsync(cursor, stop.Token);
        f.S.Events.SignalUser("other"); Check(!wait.IsCompleted, "Foreign account woke this cursor.");
        stop.Cancel(); bool cancelled = false;
        try { await wait; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && WaiterCount(f.S.Events) == 0 && WaiterCount(f.Chat) == 0 && WaiterCount(f.World) == 0 && WaiterCount(f.S.Notifications) == 0,
            "Cancellation leaked a notification subscription.");
        for (int i = 0; i < 25; i++) f.S.Notifications.SignalPlayerOnline("player-" + i, ["user"], DateTimeOffset.UtcNow);
        var online = await f.Notifications.WaitAsync(cursor, default);
        Check(online.Messages.Count == 20 && ((PlayerOnlineStreamEventDto)online.Messages[0].Data).OnlineUserId == "player-5"
            && online.Cursor.Players == 27, "Online queue lost its existing retention/order contract.");
    }

    private static async Task Exchange()
    {
        var f = new Fixture(); var owner = f.S.Issue(); var socket = new ScriptedSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var task = f.Start(socket, owner.Session, stop.Token);
        var connected = await Read(socket, stop.Token);
        Check(connected.Name == "presence" && connected.Data.GetProperty("Online").GetBoolean(), "Initial wire presence was lost.");
        int replays = f.S.Recovery.Replays;
        foreach (string malformed in new[] { "{", "[]", "null", "1", "true", "{\"eventName\":42}", "{\"eventName\":{}}", "{\"eventName\":null}", "{\"eventName\":\"unknown\"}" })
            socket.Input.Writer.TryWrite(new(malformed));
        socket.Input.Writer.TryWrite(new("ignored", WebSocketMessageType.Binary));
        socket.Input.Writer.TryWrite(new("{\"event", End: false));
        socket.Input.Writer.TryWrite(new("Name\":\"ping\"}"));
        Check((await Read(socket, stop.Token)).Name == "pong", "Malformed frames terminated the stream or fragmented ping was lost.");
        foreach (string alias in new[] { "event", "type" })
        {
            socket.Input.Writer.TryWrite(new("{\"" + alias + "\":\"ping\"}"));
            Check((await Read(socket, stop.Token)).Name == "pong", "Legacy ping alias failed.");
        }
        f.S.Events.SignalUser("user"); f.Chat.SignalUser("user"); f.World.SignalUser("user");
        f.S.Notifications.SignalPlayerOnline("friend", ["user"], DateTimeOffset.UtcNow);
        socket.Input.Writer.TryWrite(new("{\"eventName\":\"ping\"}"));
        var received = new List<string>();
        for (int i = 0; i < 5; i++) received.Add((await Read(socket, stop.Token)).Name);
        Check(received.Order().SequenceEqual(new[] { "chatChanged", "ledgerChanged", "playerOnline", "pong", "worldConfigurationChanged" }.Order())
            && socket.MaxConcurrentSends == 1 && f.S.Recovery.Replays == replays, "Concurrent push/heartbeat lost frames, overlapped sends or restarted recovery.");
        socket.Input.Writer.TryWrite(new("", WebSocketMessageType.Close));
        await task.WaitAsync(stop.Token);
        Check(f.S.Retired(owner) && socket.Disposed && !f.S.Presence.IsUserOnline("user"), "Normal close leaked resources.");
    }

    private static async Task InvalidatedDuringWait(bool endSession)
    {
        var f = new Fixture(); var owner = f.S.Issue(); var socket = new ScriptedSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var task = f.Start(socket, owner.Session, stop.Token);
        await Read(socket, stop.Token);
        if (endSession) f.S.Sessions.EndUser("user"); else f.S.Presence.ForceDisconnect("user");
        f.S.Events.SignalUser("user");
        var error = await Read(socket, stop.Token);
        Check(error.Name == "error" && !error.Data.GetProperty("Online").GetBoolean(), "Notification escaped after connection invalidation.");
        await task.WaitAsync(stop.Token);
        Check(f.S.Retired(owner) && socket.Disposed && !socket.Output.Reader.TryRead(out _), "Invalid connection leaked state or sent later notifications.");
    }

    private static async Task InvalidateOnlineBatch()
    {
        var f = new Fixture(); var owner = f.S.Issue(); var socket = new ScriptedSocket();
        socket.Sent = text =>
        {
            using var envelope = System.Text.Json.JsonDocument.Parse(text);
            string? name = envelope.RootElement.GetProperty("eventName").GetString();
            if (name == "presence")
            {
                f.S.Notifications.SignalPlayerOnline("first", ["user"], DateTimeOffset.UtcNow);
                f.S.Notifications.SignalPlayerOnline("second", ["user"], DateTimeOffset.UtcNow);
            }
            if (name == "playerOnline") f.S.Sessions.EndUser("user");
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var task = f.Start(socket, owner.Session, stop.Token);
        await Read(socket, stop.Token);
        Check((await Read(socket, stop.Token)).Data.GetProperty("OnlineUserId").GetString() == "first", "First online notification was lost.");
        Check((await Read(socket, stop.Token)).Name == "error", "Remaining batch continued after session invalidation.");
        await task.WaitAsync(stop.Token);
        Check(f.S.Retired(owner) && !socket.Output.Reader.TryRead(out _), "Obsolete batch sent later frames or retained ownership.");
    }

    private static async Task Cleanup(string failure)
    {
        var f = new Fixture(); var owner = f.S.Issue(); var socket = new ScriptedSocket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        if (failure == "accept") f.AcceptFailure = new IOException("accept failed");
        if (failure.StartsWith("receive", StringComparison.Ordinal)) socket.Input.Writer.TryWrite(new("", Failure: failure == "receive" ? new IOException("receive failed") : new InvalidOperationException("receiver failed")));
        if (failure == "send") socket.SendFailure = new IOException("send failed");
        if (failure.StartsWith("close", StringComparison.Ordinal)) socket.CloseFailure = failure == "close" ? new IOException("close failed") : new InvalidOperationException("close failed");
        if (failure == "dispose") socket.DisposeFailure = new IOException("dispose failed");
        int logs = 0;
        f.Log = () => { logs++; if ((failure == "log-open" && logs == 1) || (failure == "log-close" && logs == 2)) throw new IOException("logger failed"); };
        var task = f.Start(socket, owner.Session, stop.Token);
        if (failure is "close" or "close-invalid" or "dispose" or "recovery" or "log-close")
        {
            await Read(socket, stop.Token);
            if (failure == "recovery") f.S.Recovery.OnReplay = () => throw new IOException("close recovery failed");
            socket.Input.Writer.TryWrite(new("", WebSocketMessageType.Close));
        }
        Exception? observed = null;
        try { await task.WaitAsync(stop.Token); } catch (Exception ex) { observed = ex; }
        Check(observed is not OperationCanceledException && (failure == "close" || observed is not null), "Cleanup hung or swallowed an unrelated failure: " + failure);
        Check(f.S.Retired(owner) && !f.S.Presence.IsUserOnline("user") && (socket.Disposed || failure == "accept"), "Cleanup skipped owned resources: " + failure);
        if (failure.StartsWith("close", StringComparison.Ordinal)) Check(socket.CloseTokenCanCancel, "Closing frame used an unlimited wait.");
    }

    private static async Task CancelWhileWaiting(bool applicationStop)
    {
        var f = new Fixture(); var owner = f.S.Issue(); var socket = new ScriptedSocket();
        using var stop = new CancellationTokenSource();
        var task = f.Start(socket, owner.Session, stop.Token, applicationStop);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Read(socket, deadline.Token);
        stop.Cancel(); await task.WaitAsync(deadline.Token);
        Check(f.S.Retired(owner) && socket.Disposed && WaiterCount(f.S.Events) == 0 && WaiterCount(f.Chat) == 0
            && WaiterCount(f.World) == 0 && WaiterCount(f.S.Notifications) == 0, "Request/host cancellation left a connection or waiter.");
    }

    private static async Task BoundedClose()
    {
        var f = new Fixture(); var owner = f.S.Issue(); var socket = new ScriptedSocket { BlockClose = true };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var task = f.Start(socket, owner.Session, stop.Token);
        await Read(socket, stop.Token);
        socket.Input.Writer.TryWrite(new("", WebSocketMessageType.Close));
        await socket.CloseStarted.Task.WaitAsync(stop.Token);
        Check(f.S.Retired(owner) && !f.S.Presence.IsUserOnline("user"), "Network shutdown delayed session ownership release.");
        await task.WaitAsync(stop.Token);
        Check(socket.Disposed && socket.CloseTokenCanCancel, "Nonresponsive close was not bounded and disposed.");
    }

    private static async Task Presence()
    {
        var f = new Fixture(); var owner = f.S.Issue();
        using var stop = new CancellationTokenSource();
        var service = new SessionPresence(f.S.Connections, () => { stop.Cancel(); return NullLogger.Instance; });
        var response = await service.MaintainAsync(new("user", "colony", "base", owner.Session), stop.Token);
        Check(response.Result.Accepted && !response.Online && f.S.Retired(owner) && !f.S.Presence.IsUserOnline("user"), "Presence cancellation lost cleanup.");
        int replays = f.S.Recovery.Replays;
        response = await service.MaintainAsync(new("user", "wrong", null, owner.Session), default);
        Check(!response.Result.Accepted && f.S.Recovery.Replays == replays, "Invalid presence recovered unrelated state.");
        owner = f.S.Issue();
        Check(f.S.Connections.Open("user", "colony", owner.Session, null, out var lease) is null, "Cannot open heartbeat fixture.");
        replays = f.S.Recovery.Replays;
        Check(f.S.Connections.Check("user", "colony", owner.Session, lease!) is null && f.S.Connections.Heartbeat("user", "colony", owner.Session, lease!) is null
            && f.S.Recovery.Replays == replays, "Heartbeat ran recovery or rejected its owner.");
        f.S.Sessions.EndUser("user"); f.S.Presence.ForceDisconnect("user");
        var next = f.S.Issue();
        Check(f.S.Connections.Open("user", "colony", next.Session, null, out var nextLease) is null, "Cannot replace heartbeat fixture.");
        Check(f.S.Connections.Heartbeat("user", "colony", owner.Session, lease!) is not null && f.S.Valid(next) && nextLease!.IsActive(), "Old heartbeat touched replacement.");
        f.S.Connections.Close("user", "colony", owner.Session, lease);
        Check(f.S.Valid(next) && nextLease!.IsActive(), "Old close touched replacement.");
        f.S.Connections.Close("user", "colony", next.Session, nextLease);
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly EventNotificationHub Chat = new(), World = new();
        internal readonly SessionNotifications Notifications;
        internal Action? Log;
        internal Exception? AcceptFailure;
        internal Fixture() => Notifications = new(S.Events, Chat, World, S.Notifications);
        internal Task Start(ScriptedSocket socket, string session, CancellationToken token, bool applicationStop = false)
        {
            var context = Context(socket, session); context.RequestAborted = applicationStop ? default : token;
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket, AcceptFailure));
            return SessionEndpoints.StreamAsync(context, new Lifetime(applicationStop ? token : default), S.Connections, Notifications, () => { Log?.Invoke(); return NullLogger.Instance; });
        }
    }
    private static int WaiterCount(object hub) => ((System.Collections.IDictionary)hub.GetType().GetField("waiters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hub)!).Count;
    private static async Task<(string Name, System.Text.Json.JsonElement Data)> Read(ScriptedSocket socket, CancellationToken token)
    {
        using var envelope = System.Text.Json.JsonDocument.Parse(await socket.Output.Reader.ReadAsync(token));
        var root = envelope.RootElement;
        Check(root.GetProperty("data").ValueKind == System.Text.Json.JsonValueKind.String, "Wire data changed from a nested JSON string.");
        using var payload = System.Text.Json.JsonDocument.Parse(root.GetProperty("data").GetString()!);
        return (root.GetProperty("eventName").GetString()!, payload.RootElement.Clone());
    }

    private static DefaultHttpContext Context(ScriptedSocket socket, string session)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?userId=user&colonyId=colony&sessionId=" + Uri.EscapeDataString(session));
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
        return context;
    }
    private sealed class SocketFeature(WebSocket socket, Exception? failure = null) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => failure is null ? Task.FromResult(socket) : Task.FromException<WebSocket>(failure);
    }
    private sealed class Lifetime(CancellationToken stopping = default) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    internal sealed record Frame(string Text, WebSocketMessageType Type = WebSocketMessageType.Text, bool End = true, Exception? Failure = null);
    internal sealed class ScriptedSocket : WebSocket
    {
        internal readonly Channel<Frame> Input = Channel.CreateUnbounded<Frame>();
        internal readonly Channel<string> Output = Channel.CreateUnbounded<string>();
        private WebSocketState state = WebSocketState.Open;
        internal bool Disposed, CloseTokenCanCancel, BlockClose;
        internal readonly TaskCompletionSource CloseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? SendFailure, CloseFailure, DisposeFailure;
        internal Action<string>? Sent;
        private int sending;
        internal int MaxConcurrentSends;
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() { Disposed = true; state = WebSocketState.Closed; if (DisposeFailure is not null) throw DisposeFailure; }
        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseTokenCanCancel = cancellationToken.CanBeCanceled; CloseStarted.TrySetResult();
            if (CloseFailure is not null) throw CloseFailure;
            if (BlockClose) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            state = WebSocketState.Closed;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await Input.Reader.ReadAsync(cancellationToken);
            if (frame.Failure is not null) throw frame.Failure;
            byte[] bytes = Encoding.UTF8.GetBytes(frame.Text);
            if (bytes.Length > buffer.Count) throw new Exception("Fixture frame exceeds receive buffer.");
            bytes.CopyTo(buffer.AsSpan());
            if (frame.Type == WebSocketMessageType.Close) state = WebSocketState.CloseReceived;
            return new(bytes.Length, frame.Type, frame.End);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref sending); MaxConcurrentSends = Math.Max(MaxConcurrentSends, active);
            try { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); if (SendFailure is not null) throw SendFailure;
                string text = Encoding.UTF8.GetString(buffer); Output.Writer.TryWrite(text); Sent?.Invoke(text); }
            finally { Interlocked.Decrement(ref sending); }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
