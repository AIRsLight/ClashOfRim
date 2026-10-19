using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class SessionWebSocketTransportTests
{
    private static readonly ClashOfRimClientNetworkContext Context = new("https://localhost/base", "alice + 中", "colony&1", "snapshot/1", null, null, "owner");
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var errors = new List<Exception>();
        foreach (var test in new Func<Task>[] { UnconfiguredContext, HeartbeatFailure, CancelPendingReceive, WireAndRemoteClose,
            CloseTimeout, FailureMapping, LateReceiveAndConnect, SessionReset, CallbackFailure, ConnectionReportsFailure })
        {
            ClientSessionWork.Reset();
            try { await test(); }
            catch (Exception ex) { Console.WriteLine(test.Method.Name + ": " + ex.Message); errors.Add(ex); }
            finally { ClientSessionWork.Reset(); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    private static async Task UnconfiguredContext()
    {
        using var socket = new Socket();
        var factory = new Factory(socket) { Error = new InvalidOperationException("Unconfigured context opened a socket.") };
        string message = "未配置";
        var transport = new ClientSessionWebSocketTransport(factory, new Metadata(), () => message);
        foreach (string missing in new[] { "user", "colony", "server" })
        foreach (bool canceled in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            if (canceled) cancellation.Cancel();
            var context = new ClashOfRimClientNetworkContext(missing == "server" ? " " : Context.ServerBaseUrl,
                missing == "user" ? " " : Context.UserId, missing == "colony" ? " " : Context.ColonyId,
                Context.CurrentSnapshotId, null, null, Context.AuthToken);
            var result = await transport.StreamAsync(context, "session", 0, 0,
                _ => throw new InvalidOperationException("Unconfigured stream published an event."), cancellation.Token);
            Require(!result.Success && result.ErrorCode == "NotConfigured" && result.Message == message && factory.Uri is null,
                "Stream admission depends on the legacy facade: " + missing + ", canceled=" + canceled);
            message += ".";
        }
    }

    private static async Task HeartbeatFailure()
    {
        using var socket = new Socket { SendError = new WebSocketException("ping failed") };
        var factory = new Factory(socket);
        Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> work = Stream(factory);
        try
        {
            var result = await Finish(work, "Heartbeat failure left the receive task running.");
            Require(!result.Success && result.ErrorCode == nameof(WebSocketException), "Heartbeat failure was swallowed.");
            Require(socket.ActiveReceives == 0 && socket.DisposeCount == 1, "Heartbeat failure leaked socket work.");
        }
        finally { socket.Abort(); await Observe(work); }
    }

    private static async Task CancelPendingReceive()
    {
        using var socket = new Socket { StallClose = true };
        using var cancellation = new CancellationTokenSource();
        Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> work = Stream(new Factory(socket), cancellation.Token);
        await socket.Receiving.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            var result = await Finish(work, "Cancellation entered an unbounded close handshake.");
            Require(result.Success && result.Response!.Cancelled && socket.ActiveReceives == 0, "Cancellation did not terminate owned I/O.");
        }
        finally { socket.Abort(); await Observe(work); }
    }

    private static async Task WireAndRemoteClose()
    {
        using var socket = new Socket();
        var factory = new Factory(socket);
        var events = new List<ModSessionStreamEvent>();
        CultureInfo previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
        culture.NumberFormat.NegativeSign = "neg";
        Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> work;
        try
        {
            CultureInfo.CurrentCulture = culture;
            work = new ClientSessionWebSocketTransport(factory, new Metadata(), NotConfigured).StreamAsync(Context, "session?1", -12, 34, events.Add);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        await socket.Receiving.Task;
        socket.Text("{\"eventName\":\"pong\",\"data\":\"{}\"}");
        socket.Text("{\"eventName\":\"keepalive\",\"data\":\"{}\"}");
        byte[] text = Encoding.UTF8.GetBytes("{\"eventName\":\"notice\",\"data\":\"中文 à\"}");
        int split = Array.IndexOf(text, (byte)0xe4) + 1;
        socket.Queue(new(text[..split], WebSocketMessageType.Text, false));
        socket.Queue(new(text[split..], WebSocketMessageType.Text));
        socket.Text("{\"eventName\":\"future-event\",\"data\":\"second\"}");
        socket.RemoteClose();
        var result = await Finish(work, "Peer close did not end the stream.");
        Require(result.Success && !result.Response!.Cancelled, "Peer close was treated as a failure/cancellation.");
        Require(events.Count == 2 && events[0].EventName == "notice" && events[0].Data == "中文 à"
            && events[1].EventName == "future-event", "Fragmented UTF-8, event ordering or keepalive filtering changed.");
        string route = ProtocolContractManifest.Find(ProtocolMessageKind.StreamSession).Route;
        Require(factory.Uri!.Scheme == "wss" && factory.Uri.AbsolutePath == "/base/" + route.TrimStart('/')
            && factory.Uri.Query == "?userId=alice%20%2B%20%E4%B8%AD&colonyId=colony%261&knownNotificationVersion=-12&knownWorldConfigurationVersion=34&currentSnapshotId=snapshot%2F1&sessionId=session%3F1",
            "Stream query, invariant version formatting or base path changed.");
        Require(factory.Language == ServerLocalization.EncodeLanguageHeader("简体中文")
            && socket.Sent.Single() == "{\"eventName\":\"ping\",\"data\":\"{}\"}", "Language encoding or heartbeat wire contract changed.");
        Require(socket.CloseOutputCount == 1 && socket.CloseCount == 0 && socket.DisposeCount == 1
            && socket.ActiveReceives == 0 && socket.ActiveSends == 0, "Remote close left work running or started a second receive handshake.");

        using var minimal = new Socket { StallSend = true };
        var minimalFactory = new Factory(minimal);
        work = new ClientSessionWebSocketTransport(minimalFactory, new Metadata(null), NotConfigured).StreamAsync(
            new ClashOfRimClientNetworkContext("http://localhost:8123", "a", "b", null, null, null, null), null, 0, 0, _ => { });
        minimal.RemoteClose();
        result = await Finish(work, "Remote close did not cancel an in-flight send.");
        Require(result.Success && minimal.ActiveSends == 0 && minimal.ActiveReceives == 0
            && minimalFactory.Uri!.Scheme == "ws" && minimalFactory.Uri.Port == 8123 && minimalFactory.Language is null
            && !minimalFactory.Uri.Query.Contains("sessionId") && !minimalFactory.Uri.Query.Contains("currentSnapshotId"),
            "Optional stream metadata or send cancellation changed.");
    }

    private static async Task CloseTimeout()
    {
        using var socket = new Socket { StallClose = true };
        var work = new ClientSessionWebSocketTransport(new Factory(socket), new Metadata(), NotConfigured, TimeSpan.FromMilliseconds(30))
            .StreamAsync(Context, "session", 0, 0, _ => { });
        socket.RemoteClose();
        try
        {
            var result = await Finish(work, "The close reply ignored its deadline.");
            Require(!result.Success && result.ErrorCode == nameof(TimeoutException) && socket.DisposeCount == 1
                && socket.CloseOutputCount == 1 && socket.State == WebSocketState.Aborted, "Close timeout did not fail and release the socket.");
        }
        finally { socket.Abort(); await Observe(work); }
    }

    private static async Task FailureMapping()
    {
        foreach (string failure in new[] { "connect", "receive", "both", "binary", "empty", "invalid", "null" })
        {
            using var socket = new Socket();
            var factory = new Factory(socket);
            string expected;
            if (failure == "connect") { factory.Error = new WebSocketException("connect failed"); expected = nameof(WebSocketException); }
            else if (failure is "receive" or "both")
            {
                socket.ReceiveError = new IOException("receive failed");
                if (failure == "both") socket.SendError = new WebSocketException("secondary send failed");
                expected = nameof(IOException);
            }
            else if (failure == "binary") { socket.Queue(new(new byte[] { 1 }, WebSocketMessageType.Binary)); expected = nameof(InvalidDataException); }
            else
            {
                socket.Text(failure switch { "empty" => "", "invalid" => "{invalid", _ => "null" });
                expected = failure == "null" ? nameof(InvalidOperationException) : nameof(System.Runtime.Serialization.SerializationException);
            }
            var result = await Finish(Stream(factory), failure + " left the stream running.");
            Require(!result.Success && result.ErrorCode == expected, failure + " did not preserve its failure classification: " + result.ErrorCode);
            Require(socket.DisposeCount == (failure == "connect" ? 0 : 1) && socket.ActiveReceives == 0
                && socket.ActiveSends == 0 && socket.CloseCount == 0 && socket.CloseOutputCount == 0, failure + " leaked work or attempted a graceful handshake.");
        }
    }

    private static async Task LateReceiveAndConnect()
    {
        foreach (bool reset in new[] { false, true })
        {
            using var socket = new Socket { LateReceive = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var cancellation = new CancellationTokenSource();
            int events = 0;
            var work = new ClientSessionWebSocketTransport(new Factory(socket), new Metadata(), NotConfigured)
                .StreamAsync(Context, "session", 0, 0, _ => events++, cancellation.Token);
            await socket.Receiving.Task;
            if (reset) ClientSessionWork.Reset(); else cancellation.Cancel();
            // Deliberately model a completed read racing cancellation, even if it ignored abort.
            socket.LateReceive.SetResult(new(Encoding.UTF8.GetBytes("{\"eventName\":\"late\",\"data\":\"{}\"}"), WebSocketMessageType.Text));
            var result = await Finish(work, "Late read prevented cancellation.");
            Require(result.Success && result.Response!.Cancelled && events == 0 && socket.ActiveReceives == 0, "A stale read published into a replacement session.");
        }

        using var connected = new Socket();
        using var cancelled = new CancellationTokenSource();
        var opening = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory(connected) { Open = _ => opening.Task };
        var pending = Stream(factory, cancelled.Token);
        cancelled.Cancel();
        opening.SetResult(connected);
        var late = await Finish(pending, "Late connect remained open.");
        Require(late.Success && late.Response!.Cancelled && connected.DisposeCount == 1 && connected.Sent.Count == 0, "Canceled connect started new socket work.");
    }

    private static async Task SessionReset()
    {
        using var socket = new Socket { StallClose = true };
        var work = Stream(new Factory(socket));
        await socket.Receiving.Task;
        ClientSessionWork.Reset();
        var result = await Finish(work, "Session reset did not terminate the stream.");
        Require(result.Success && result.Response!.Cancelled && socket.ActiveReceives == 0
            && socket.CloseCount == 0 && socket.CloseOutputCount == 0, "Session reset attempted a graceful handshake.");
    }

    private static async Task CallbackFailure()
    {
        using var socket = new Socket { StallSend = true };
        var expected = new ApplicationException("subscriber failed");
        var work = new ClientSessionWebSocketTransport(new Factory(socket), new Metadata(), NotConfigured)
            .StreamAsync(Context, "session", 0, 0, _ => throw expected);
        socket.Text("{\"eventName\":\"notice\",\"data\":\"{}\"}");
        try { await Finish(work, "Callback failure left the ping task running."); throw new Exception("Callback failure was swallowed."); }
        catch (ApplicationException ex) when (ReferenceEquals(ex, expected)) { }
        Require(socket.ActiveSends == 0 && socket.ActiveReceives == 0 && socket.DisposeCount == 1, "Callback failure leaked I/O.");
    }

    private static async Task ConnectionReportsFailure()
    {
        using var socket = new Socket { SendError = new WebSocketException("ping failed") };
        var connection = new ClientSessionConnection(new ClientSessionWebSocketTransport(new Factory(socket), new Metadata(), NotConfigured));
        var statuses = new List<SessionConnectionStatus>();
        string? lost = null;
        connection.StatusChanged += (status, _) => statuses.Add(status);
        connection.ConnectionLost += source => lost = source.SessionId;
        await connection.Start(Context, "owned-session", 0, 0).WaitAsync(TimeSpan.FromSeconds(2));
        Require(!connection.IsRunning && lost == "owned-session"
            && statuses.Last().Phase == SessionConnectionPhase.Failed && statuses.Last().ErrorCode == nameof(WebSocketException),
            "Transport failure did not reach the reconnect coordinator.");
    }

    private static Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> Stream(Factory factory, CancellationToken token = default) =>
        new ClientSessionWebSocketTransport(factory, new Metadata(), NotConfigured).StreamAsync(Context, "session?1", 12, 34, _ => { }, token);
    private static async Task<T> Finish<T>(Task<T> work, string failure)
    {
        if (await Task.WhenAny(work, Task.Delay(1500)) != work) throw new Exception(failure);
        return await work;
    }
    private static async Task Observe(Task work) { try { await work.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private static string NotConfigured() => "Not configured";
    private sealed class Metadata : IClientProtocolMetadata
    {
        internal Metadata(string? language = "简体中文") => CurrentLanguage = language;
        public string? CurrentLanguage { get; }
        public string DeserializationFailure(string responseType) => "Invalid " + responseType;
    }
    private sealed class Factory : IClientWebSocketFactory
    {
        private readonly Socket socket;
        internal Factory(Socket socket) => this.socket = socket;
        internal Uri? Uri;
        internal string? Language;
        internal Exception? Error;
        internal Func<CancellationToken, Task<WebSocket>>? Open;
        public Task<WebSocket> OpenAsync(Uri uri, string? encodedLanguage, CancellationToken token)
        {
            Uri = uri; Language = encodedLanguage;
            token.ThrowIfCancellationRequested();
            return Open?.Invoke(token) ?? (Error is null ? Task.FromResult<WebSocket>(socket) : Task.FromException<WebSocket>(Error));
        }
    }
    private sealed record Frame(byte[] Data, WebSocketMessageType Type, bool End = true);
    private sealed class Socket : WebSocket
    {
        private readonly Channel<Frame> incoming = Channel.CreateUnbounded<Frame>();
        private readonly CancellationTokenSource aborted = new();
        private WebSocketState state = WebSocketState.Open;
        internal readonly TaskCompletionSource Receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Sent = new();
        internal Exception? SendError;
        internal Exception? ReceiveError;
        internal TaskCompletionSource<Frame>? LateReceive;
        internal bool StallClose, StallSend;
        internal int ActiveReceives, ActiveSends, DisposeCount, CloseCount, CloseOutputCount;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => state;
        internal void Queue(Frame frame) => incoming.Writer.TryWrite(frame);
        internal void Text(string text) => Queue(new(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));
        internal void RemoteClose() => Queue(new(Array.Empty<byte>(), WebSocketMessageType.Close));
        public override void Abort() { state = WebSocketState.Aborted; aborted.Cancel(); }
        public override void Dispose() { DisposeCount++; Abort(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        { CloseCount++; return Close(token); }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        { CloseOutputCount++; return Close(token); }
        private async Task Close(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
            if (StallClose) await Task.Delay(Timeout.Infinite, linked.Token);
            state = WebSocketState.Closed;
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            Interlocked.Increment(ref ActiveSends);
            try
            {
                token.ThrowIfCancellationRequested();
                Require(type == WebSocketMessageType.Text && endOfMessage, "Ping framing changed.");
                Sent.Add(Encoding.UTF8.GetString(buffer));
                if (SendError is not null) throw SendError;
                if (StallSend) await Task.Delay(Timeout.Infinite, token);
            }
            finally { Interlocked.Decrement(ref ActiveSends); }
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            Interlocked.Increment(ref ActiveReceives); Receiving.TrySetResult();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
                if (ReceiveError is not null) throw ReceiveError;
                Frame frame = LateReceive is null ? await incoming.Reader.ReadAsync(linked.Token) : await LateReceive.Task;
                frame.Data.AsSpan().CopyTo(buffer.AsSpan());
                if (frame.Type == WebSocketMessageType.Close) state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(frame.Data.Length, frame.Type, frame.End);
            }
            finally { Interlocked.Decrement(ref ActiveReceives); }
        }
    }
}
