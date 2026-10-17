using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

internal static class SessionConnectionModuleTests
{
    private static readonly ClashOfRimClientNetworkContext Context = new("http://localhost:12345", "user", "colony", "snapshot", null, null, "token");

    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            await InvalidStart();
            foreach (bool reset in new[] { false, true }) await ReplaceConnection(reset);
            foreach (string ending in new[] { "close", "cancel", "failure", "exception", "stop", "reset" }) await EndConnection(ending);
            await RepeatedStopCompletionRace();
            await ResetDuringScheduling();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task InvalidStart()
    {
        ClientSessionWork.Reset();
        var transport = new FakeTransport();
        var connection = new ClientSessionConnection(transport);
        var phases = new List<SessionConnectionPhase>();
        connection.StatusChanged += value => phases.Add(value.Phase);
        await connection.Start(new("", "", "", null, null, null, null), "session", 0, 0);
        await connection.Start(Context, null, 0, 0);
        Require(!connection.IsRunning && transport.Calls.IsEmpty && phases.SequenceEqual(new[] { SessionConnectionPhase.NotConfigured, SessionConnectionPhase.SessionMissing }),
            "Invalid identity started a stream.");
    }

    private static async Task ReplaceConnection(bool reset)
    {
        ClientSessionWork.Reset();
        var transport = new FakeTransport();
        var connection = new ClientSessionConnection(transport);
        var received = new ConcurrentQueue<string>();
        var lost = new ConcurrentQueue<string>();
        var phases = new ConcurrentQueue<SessionConnectionPhase>();
        connection.EventReceived += (_, session) => received.Enqueue(session);
        connection.ConnectionLost += lost.Enqueue;
        connection.StatusChanged += value => phases.Enqueue(value.Phase);
        Task oldWork = connection.Start(Context, "old", 12, 34);
        var old = await transport.Next();
        Require(old.NotificationVersion == 12 && old.WorldVersion == 34, "Stream lost resume versions.");
        old.Emit();
        if (reset) ClientSessionWork.Reset();
        transport.BeforeStart = () => Require(old.Token.IsCancellationRequested, "Replacement opened before cancelling the old stream.");
        Task newWork = connection.Start(Context, "new", 56, 78);
        var current = await transport.Next();
        Require(old.Token.IsCancellationRequested && connection.IsRunning, "Replacement did not own cancellation and running state.");
        old.Emit(); // Even callbacks invoked outside their original ExecutionContext must be rejected.
        old.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed("late", "old stream failed"));
        await oldWork.WaitAsync(TimeSpan.FromSeconds(5));
        current.Emit();
        Require(connection.IsRunning && received.SequenceEqual(new[] { "old", "new" }) && lost.IsEmpty
            && !phases.Contains(SessionConnectionPhase.Failed), "Old completion/event/status escaped connection ownership.");
        connection.Stop();
        current.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
        await newWork.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!connection.IsRunning && lost.IsEmpty, "Explicit stop triggered connection-loss recovery.");
    }

    private static async Task EndConnection(string ending)
    {
        ClientSessionWork.Reset();
        var transport = new FakeTransport();
        var connection = new ClientSessionConnection(transport);
        int received = 0, lost = 0;
        connection.EventReceived += (_, _) => received++;
        connection.ConnectionLost += _ => lost++;
        Task work = connection.Start(Context, "session", 0, 0);
        var pending = await transport.Next();
        if (ending == "stop") { connection.Stop(); Require(pending.Token.IsCancellationRequested, "Stop did not cancel I/O."); }
        if (ending == "reset") { ClientSessionWork.Reset(); pending.Emit(); Require(received == 0, "Expired generation published a callback."); }
        if (ending == "exception") pending.Result.SetException(new InvalidOperationException("stream failure"));
        else if (ending == "failure") pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed("network", "failed"));
        else pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(ending == "cancel")));
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!connection.IsRunning && lost == (ending is "close" or "failure" or "exception" ? 1 : 0), "Wrong connection termination semantics: " + ending);
        connection.Stop(); // A completed/expired connection must not retain a disposed cancellation source.
    }

    private static async Task RepeatedStopCompletionRace()
    {
        ClientSessionWork.Reset();
        var transport = new FakeTransport();
        var connection = new ClientSessionConnection(transport);
        for (int i = 0; i < 40; i++)
        {
            Task work = connection.Start(Context, "session", 0, 0);
            var pending = await transport.Next();
            await Task.WhenAll(Task.Run(() => connection.Stop()), Task.Run(() => pending.Result.SetResult(
                ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)))));
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!connection.IsRunning, "Stop/completion race retained the connection.");
        }
    }

    private sealed class FakeTransport : IClientSessionStreamTransport
    {
        internal readonly ConcurrentQueue<Pending> Calls = new();
        internal Action? BeforeStart;
        private readonly SemaphoreSlim available = new(0);
        public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(
            ClashOfRimClientNetworkContext context, string sessionId, long notificationVersion, long worldVersion,
            Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken)
        {
            BeforeStart?.Invoke();
            var pending = new Pending(onEvent, cancellationToken, notificationVersion, worldVersion);
            Calls.Enqueue(pending); available.Release();
            return pending.Result.Task;
        }
        internal async Task<Pending> Next()
        {
            Require(await available.WaitAsync(TimeSpan.FromSeconds(5)), "Stream did not start.");
            Require(Calls.TryDequeue(out var value), "Missing stream invocation.");
            return value!;
        }
    }

    private static async Task ResetDuringScheduling()
    {
        // Exercise both already-started and skipped delegates without depending on worker timing.
        var connection = new ClientSessionConnection(new ImmediateTransport());
        for (int i = 0; i < 64; i++)
        {
            Task work = connection.Start(Context, "scheduled", 0, 0);
            ClientSessionWork.Reset();
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!connection.IsRunning, "Session reset before/during scheduling stranded connection ownership.");
        }
    }

    private sealed class ImmediateTransport : IClientSessionStreamTransport
    {
        public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(
            ClashOfRimClientNetworkContext context, string sessionId, long notificationVersion, long worldVersion,
            Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken)
            => Task.FromResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
    }

    private sealed record Pending(Action<ModSessionStreamEvent> Callback, CancellationToken Token, long NotificationVersion, long WorldVersion)
    {
        internal readonly TaskCompletionSource<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Emit() => Callback(new ModSessionStreamEvent("probe", "{}"));
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
