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
            await ObserverFailureCleanup();
            foreach (var phase in new[] { SessionConnectionPhase.Connecting, SessionConnectionPhase.Disconnecting, SessionConnectionPhase.Closed,
                SessionConnectionPhase.Failed, SessionConnectionPhase.Exception, SessionConnectionPhase.SessionMissing, SessionConnectionPhase.NotConfigured })
                foreach (string change in new[] { "replace", "stop", "reset", "diagnostic" }) await ReentrantStatusSubscribers(phase, change);
            await ReentrantLossSubscribers();
            foreach (bool cancelCallback in new[] { false, true }) await ReentrantStopCancellation(cancelCallback);
            foreach (bool replace in new[] { false, true }) await ThrowAfterReplacement(replace);
            foreach (string ending in new[] { "close", "failure", "exception" })
                foreach (bool throwLoss in new[] { false, true }) await TerminalObserverFailure(ending, throwLoss);
            await InvalidStart();
            foreach (bool reset in new[] { false, true }) await ReplaceConnection(reset);
            foreach (string ending in new[] { "close", "cancel", "failure", "exception", "stop", "reset" }) await EndConnection(ending);
            await RepeatedStopCompletionRace();
            await ResetDuringScheduling();
            foreach (bool replace in new[] { false, true }) await ReentrantStart(replace);
            foreach (string ending in new[] { "close", "failure", "exception" }) await ReentrantCompletion(ending);
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task ReentrantStatusSubscribers(SessionConnectionPhase phase, string change)
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        Task? original = null, replacement = null; SessionConnectionTestTransport.Pending? pending = null;
        if (phase is SessionConnectionPhase.Disconnecting or SessionConnectionPhase.Closed or SessionConnectionPhase.Failed or SessionConnectionPhase.Exception)
        { original = connection.Start(Context, "same", 0, 0); pending = await transport.Next(); }
        SessionConnectionStatus? trigger = null; ClientSessionConnection.Publication? receipt = null; int stale = 0;
        connection.StatusChanged += (status, source) =>
        {
            if (trigger is not null || status.Phase != phase) return;
            trigger = status; receipt = source;
            switch (change)
            {
                case "replace": replacement = connection.Start(Context, "same", 0, 0); break;
                case "stop": connection.Stop(); break;
                case "reset": ClientSessionWork.Reset(); break;
                case "diagnostic": _ = connection.Start(Context, null, 0, 0); break;
            }
        };
        connection.StatusChanged += (status, _) => { if (ReferenceEquals(status, trigger)) stale++; };
        if (phase == SessionConnectionPhase.Connecting) original = connection.Start(Context, "same", 0, 0);
        else if (phase == SessionConnectionPhase.SessionMissing) await connection.Start(Context, null, 0, 0);
        else if (phase == SessionConnectionPhase.NotConfigured) await connection.Start(new("", "", "", null, null, null, null), "same", 0, 0);
        else if (phase == SessionConnectionPhase.Disconnecting) connection.Stop();
        if (pending is not null)
        {
            if (phase == SessionConnectionPhase.Exception) pending.Result.SetException(new InvalidOperationException("transport"));
            else pending.Result.SetResult(phase == SessionConnectionPhase.Failed ? ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed("network", "failure")
                : ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(phase == SessionConnectionPhase.Disconnecting)));
            await original!;
        }
        // An invalid start changes only the displayed status; it must not stop the live stream.
        if (phase == SessionConnectionPhase.Connecting && change == "diagnostic")
        {
            var active = await transport.Next(); connection.Stop(false);
            active.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await original!;
        }
        else if (phase == SessionConnectionPhase.Connecting) await original!;
        bool replayed = false; receipt?.TryApply(() => replayed = true);
        Require(trigger is not null && stale == 0 && !replayed, "Superseded status reached another subscriber or replay: " + phase + "/" + change);
        if (replacement is not null)
        {
            var active = await transport.Next(); Require(connection.IsRunning, "Stale completion removed replacement.");
            connection.Stop(false); active.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await replacement;
        }
        connection.Stop(false);
    }

    private static async Task ReentrantLossSubscribers()
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        Task? replacement = null; int stale = 0; ClientSessionConnection.Observation? receipt = null;
        connection.ConnectionLost += source => { receipt = source; replacement = connection.Start(Context, "same", 0, 0); };
        connection.ConnectionLost += _ => stale++;
        var work = connection.Start(Context, "same", 0, 0); var pending = await transport.Next();
        pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(false))); await work;
        bool replayed = false; receipt!.TryApply(() => replayed = true);
        var active = await transport.Next();
        Require(stale == 0 && !replayed && connection.IsRunning && receipt.Context.AuthToken == Context.AuthToken, "Old loss callback escaped source ownership.");
        connection.Stop(false); active.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await replacement!;
    }

    private static async Task ReentrantStopCancellation(bool cancelCallback)
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        var work = connection.Start(Context, "same", 0, 0); var pending = await transport.Next();
        Task? replacement = null; int stopping = 0;
        void Replace() { Require(pending.Token.IsCancellationRequested, "Stop observer reopened before source cancellation."); replacement = connection.Start(Context, "same", 0, 0); }
        using var registration = cancelCallback ? pending.Token.Register(Replace) : default;
        connection.StatusChanged += (status, _) => { if (status.Phase == SessionConnectionPhase.Disconnecting) { stopping++; if (!cancelCallback) Replace(); } };
        connection.Stop(); var active = await transport.Next();
        pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await work;
        Require(stopping == (cancelCallback ? 0 : 1) && connection.IsRunning, "Stop callback overrode its replacement.");
        connection.Stop(false); active.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await replacement!;
    }

    private static async Task ThrowAfterReplacement(bool stop)
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        Task? original = null, replacement = null; SessionConnectionTestTransport.Pending? pending = null;
        if (stop) { original = connection.Start(Context, "same", 0, 0); pending = await transport.Next(); }
        bool reentered = false;
        connection.StatusChanged += (status, _) =>
        {
            if (reentered || status.Phase != (stop ? SessionConnectionPhase.Disconnecting : SessionConnectionPhase.Connecting)) return;
            reentered = true; replacement = connection.Start(Context, "same", 0, 0); throw new InvalidOperationException("observer");
        };
        bool threw = false;
        try { if (stop) connection.Stop(); else await connection.Start(Context, "same", 0, 0); }
        catch (InvalidOperationException ex) when (ex.Message == "observer") { threw = true; }
        if (pending is not null)
        {
            Require(pending.Token.IsCancellationRequested, "Throwing stop observer retained source I/O.");
            pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await original!;
        }
        var active = await transport.Next(); Require(threw && connection.IsRunning && transport.Calls.IsEmpty, "Throwing callback removed/opened the wrong connection.");
        connection.Stop(false); active.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await replacement!;
    }

    private static async Task TerminalObserverFailure(string ending, bool throwLoss)
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        int terminal = 0, lost = 0;
        connection.StatusChanged += (status, _) =>
        {
            if (status.Phase == SessionConnectionPhase.Connecting) return;
            terminal++; if (!throwLoss) throw new InvalidOperationException("observer");
        };
        connection.ConnectionLost += _ => { lost++; if (throwLoss) throw new InvalidOperationException("observer"); };
        var work = connection.Start(Context, "same", 0, 0); var pending = await transport.Next();
        if (ending == "exception") pending.Result.SetException(new InvalidOperationException("transport"));
        else pending.Result.SetResult(ending == "failure" ? ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed("network", "failure")
            : ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(false)));
        bool threw = false; try { await work; } catch (InvalidOperationException ex) when (ex.Message == "observer") { threw = true; }
        Require(threw && terminal == 1 && lost == 1 && !connection.IsRunning, "Observer failure replayed loss/status or stranded cleanup: " + ending);
    }

    private static async Task ObserverFailureCleanup()
    {
        foreach (var phase in new[] { SessionConnectionPhase.Connecting, SessionConnectionPhase.Disconnecting })
        {
            ClientSessionWork.Reset();
            var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
            Task? work = null; SessionConnectionTestTransport.Pending? pending = null;
            if (phase == SessionConnectionPhase.Disconnecting)
            { work = connection.Start(Context, "source", 0, 0); pending = await transport.Next(); }
            connection.StatusChanged += (status, _) => { if (status.Phase == phase) throw new InvalidOperationException("observer"); };
            bool threw = false;
            try
            {
                if (phase == SessionConnectionPhase.Connecting) await connection.Start(Context, "source", 0, 0);
                else connection.Stop();
            }
            catch (InvalidOperationException ex) when (ex.Message == "observer") { threw = true; }
            bool cleaned = !connection.IsRunning && (pending is null ? transport.Calls.IsEmpty : pending.Token.IsCancellationRequested);
            if (pending is not null) { pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await work!; }
            Require(threw && cleaned, "Status observer exception stranded connection cleanup: " + phase);
        }
    }

    private static async Task InvalidStart()
    {
        ClientSessionWork.Reset();
        var transport = new SessionConnectionTestTransport();
        var connection = new ClientSessionConnection(transport);
        var phases = new List<SessionConnectionPhase>();
        connection.StatusChanged += (value, _) => phases.Add(value.Phase);
        await connection.Start(new("", "", "", null, null, null, null), "session", 0, 0);
        await connection.Start(Context, null, 0, 0);
        Require(!connection.IsRunning && transport.Calls.IsEmpty && phases.SequenceEqual(new[] { SessionConnectionPhase.NotConfigured, SessionConnectionPhase.SessionMissing }),
            "Invalid identity started a stream.");
    }

    private static async Task ReplaceConnection(bool reset)
    {
        ClientSessionWork.Reset();
        var transport = new SessionConnectionTestTransport();
        var connection = new ClientSessionConnection(transport);
        var received = new ConcurrentQueue<string>();
        var lost = new ConcurrentQueue<string>();
        var phases = new ConcurrentQueue<SessionConnectionPhase>();
        connection.EventReceived += (_, source) => received.Enqueue(source.SessionId);
        connection.ConnectionLost += source => lost.Enqueue(source.SessionId);
        connection.StatusChanged += (value, _) => phases.Enqueue(value.Phase);
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
        var transport = new SessionConnectionTestTransport();
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
        var transport = new SessionConnectionTestTransport();
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

    private static async Task ReentrantStart(bool replace)
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        bool reentered = false; Task? next = null;
        connection.StatusChanged += (status, _) =>
        {
            if (status.Phase != SessionConnectionPhase.Connecting || reentered) return;
            reentered = true;
            if (replace) next = connection.Start(Context, "next", 0, 0); else connection.Stop();
        };
        await connection.Start(Context, "old", 0, 0).WaitAsync(TimeSpan.FromSeconds(5));
        if (replace)
        {
            var pending = await transport.Next(); Require(pending.SessionId == "next" && transport.Calls.IsEmpty, "Replaced Connecting callback still opened old transport.");
            connection.Stop(); pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await next!;
        }
        else Require(transport.Calls.IsEmpty && !connection.IsRunning, "Stopped Connecting callback still opened a stream.");
    }

    private static async Task ReentrantCompletion(string ending)
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        Task? replacement = null; int lost = 0;
        connection.StatusChanged += (status, _) =>
        {
            if (status.Phase is SessionConnectionPhase.Closed or SessionConnectionPhase.Failed or SessionConnectionPhase.Exception)
                replacement = connection.Start(Context, "same", 0, 0);
        };
        connection.ConnectionLost += _ => lost++;
        Task work = connection.Start(Context, "same", 0, 0); var source = await transport.Next();
        if (ending == "exception") source.Result.SetException(new InvalidOperationException("expected"));
        else source.Result.SetResult(ending == "close" ? ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(false))
            : ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed("network", "expected"));
        await work.WaitAsync(TimeSpan.FromSeconds(5)); var next = await transport.Next();
        Require(lost == 0 && connection.IsRunning, "Old terminal status caused recovery/cleanup against its replacement.");
        connection.Stop(); next.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await replacement!;
    }

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
