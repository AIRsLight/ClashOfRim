using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Sessions;

internal static class ClientConnectionStatusTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var original = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var phase in Enum.GetValues<SessionConnectionPhase>()) { await Display(phase); count++; }
                foreach (var phase in new[] { SessionConnectionPhase.Connecting, SessionConnectionPhase.Disconnecting, SessionConnectionPhase.Exception })
                foreach (string callback in new[] { "text", "status" })
                foreach (string change in new[] { "user", "colony", "server", "auth", "login", "generation", "stop", "diagnostic", "snapshot", "password", "steam", "game" })
                { await Reentry(phase, callback, change); count++; }
                foreach (string change in new[] { "user", "colony", "server", "auth", "login", "generation", "snapshot", "password", "steam", "game" })
                { await BeforeTerminal(change); count++; }
                foreach (string callback in new[] { "text", "status", "diagnose" })
                foreach (var phase in new[] { SessionConnectionPhase.Connecting, SessionConnectionPhase.Disconnecting, SessionConnectionPhase.Exception })
                { await Failure(phase, callback); count++; }
            }
            Console.WriteLine($"PASS: connection status ({count} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = original; ClientSessionWork.Reset(); }
    }
    private static async Task Display(SessionConnectionPhase phase)
    {
        await using var f = new Fixture(); await f.Before(phase); await f.Deliver(phase);
        string suffix = phase switch
        {
            SessionConnectionPhase.NotConfigured => "StatusNotConfigured",
            SessionConnectionPhase.SessionMissing => "StatusSessionMissing",
            SessionConnectionPhase.Connecting => "StatusConnecting",
            SessionConnectionPhase.Disconnecting => "StatusDisconnecting",
            SessionConnectionPhase.Cancelled => "StatusDisconnectedManual",
            SessionConnectionPhase.Failed => "StatusFailed",
            SessionConnectionPhase.Closed => "StatusClosed",
            SessionConnectionPhase.Exception => "StatusException",
            _ => throw new InvalidOperationException("Unknown test phase")
        };
        Require(f.View.Keys.SequenceEqual(new[] { "ClashOfRim.Presence." + suffix })
            && f.View.Statuses.Count == 1 && f.View.Statuses[0].StartsWith(CultureInfo.CurrentCulture.Name + ":", StringComparison.Ordinal), "Wrong status mapping: " + phase);
        if (phase == SessionConnectionPhase.Failed)
            Require(f.View.Args.SequenceEqual(new[] { ("CODE", (object?)"I/O"), ("MESSAGE", (object?)"连接中断 — bağlantı échec") }), "Failure detail was lost.");
        else if (phase == SessionConnectionPhase.Exception)
            Require(f.View.Args.SequenceEqual(new[] { ("TYPE", (object?)nameof(InvalidOperationException)), ("MESSAGE", (object?)"连接中断 — bağlantı échec") }), "Exception detail was lost.");
        else Require(f.View.Args.Length == 0, "Unrelated exception arguments leaked into status.");
        bool diagnostic = phase is SessionConnectionPhase.SessionMissing or SessionConnectionPhase.Connecting or SessionConnectionPhase.Exception;
        Require(f.View.Diagnostics.Count == (diagnostic ? 1 : 0), "Wrong diagnostic count: " + phase);
        if (diagnostic)
        {
            var report = f.View.Diagnostics.Single();
            Require(report.Warning == (phase != SessionConnectionPhase.Connecting), "Wrong diagnostic severity.");
            Require(!report.Text.Contains("secret-auth", StringComparison.Ordinal), "Status diagnostic exposed authentication.");
            if (phase == SessionConnectionPhase.Connecting)
                Require(report.Text.Contains("user=alice, colony=colony, session=same", StringComparison.Ordinal), "Connection diagnostic lost source identity.");
        }
    }
    private static async Task Reentry(SessionConnectionPhase phase, string callback, string change)
    {
        await using var f = new Fixture(); await f.Before(phase);
        void Change()
        {
            f.View.OnText = f.View.OnStatus = null;
            f.Change(change);
        }
        if (callback == "text") f.View.OnText = Change; else f.View.OnStatus = Change;
        await f.Deliver(phase);
        bool unrelated = IsUnrelated(change);
        if (change == "diagnostic")
        {
            Require(f.View.Statuses.Last().Contains("StatusSessionMissing", StringComparison.Ordinal)
                && f.View.Diagnostics.Count == 1 && f.View.Diagnostics[0].Text.Contains("without session id", StringComparison.Ordinal), "Older status continued after a newer diagnostic.");
        }
        else
        {
            Require(f.View.Statuses.Count == (unrelated || callback == "status" ? 1 : 0), "Status escaped reentry: " + phase + "/" + callback + "/" + change);
            Require(f.View.Diagnostics.Count == (unrelated && phase != SessionConnectionPhase.Disconnecting ? 1 : 0), "Diagnostic escaped reentry: " + phase + "/" + callback + "/" + change);
        }
    }
    private static async Task BeforeTerminal(string change)
    {
        await using var f = new Fixture(); await f.Before(SessionConnectionPhase.Exception);
        f.Change(change); await f.Deliver(SessionConnectionPhase.Exception);
        Require(f.View.Keys.Count == (IsUnrelated(change) ? 1 : 0) && f.View.Statuses.Count == (IsUnrelated(change) ? 1 : 0), "Terminal status used another identity: " + change);
    }
    private static bool IsUnrelated(string change) => change is "snapshot" or "password" or "steam" or "game";
    private static async Task Failure(SessionConnectionPhase phase, string callback)
    {
        await using var f = new Fixture(); await f.Before(phase);
        void Fail() => throw new InvalidOperationException("presentation");
        if (callback == "text") f.View.OnText = Fail;
        else if (callback == "status") f.View.OnStatus = Fail;
        else f.View.OnDiagnose = Fail;
        bool threw = false;
        try { await f.Deliver(phase); }
        catch (InvalidOperationException ex) when (ex.Message == "presentation") { threw = true; }
        bool expected = callback != "diagnose" || phase != SessionConnectionPhase.Disconnecting;
        Require(threw == expected && !f.Connection.IsRunning, "Presentation exception stranded connection or replayed work: " + phase + "/" + callback);
        if (f.Pending is { } pending && phase == SessionConnectionPhase.Disconnecting) Require(pending.Token.IsCancellationRequested, "Status exception skipped cancellation.");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly SessionConnectionTestTransport Transport = new();
        internal readonly View View = new();
        internal readonly ClientSessionConnection Connection;
        internal readonly ClientSessionLifecycle Session;
        internal SessionConnectionTestTransport.Pending? Pending;
        private Task? work;
        private readonly object cleanupGate = new();
        private bool closing;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Ports.Auth = "secret-auth";
            Transport.Started = pending =>
            {
                lock (cleanupGate)
                {
                    Pending = pending;
                    if (closing) pending.Result.TrySetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
                }
            };
            Connection = new(Transport); Session = new(Ports, Ports, Connection); Login();
            _ = new ClientConnectionStatus(Session, Connection, View);
        }
        private void Login() => Session.PublishLogin(new() { Result = new() { Accepted = true }, AuthToken = "secret-auth", SessionId = "same", AuthenticatedUserId = "alice" });
        internal async Task Before(SessionConnectionPhase phase)
        {
            if (phase is SessionConnectionPhase.NotConfigured or SessionConnectionPhase.SessionMissing or SessionConnectionPhase.Connecting) return;
            work = Connection.Start(Ports.ReadContext(), "same", 0, 0); Pending = await Transport.Next(); View.Clear();
        }
        internal async Task Deliver(SessionConnectionPhase phase)
        {
            if (phase == SessionConnectionPhase.NotConfigured) { Ports.Server = ""; await Connection.Start(Ports.ReadContext(), "same", 0, 0); }
            else if (phase == SessionConnectionPhase.SessionMissing) await Connection.Start(Ports.ReadContext(), null, 0, 0);
            else if (phase == SessionConnectionPhase.Connecting) work = Connection.Start(Ports.ReadContext(), "same", 0, 0);
            else if (phase == SessionConnectionPhase.Disconnecting) Connection.Stop();
            else
            {
                if (phase == SessionConnectionPhase.Exception) Pending!.Result.SetException(new InvalidOperationException("连接中断 — bağlantı échec"));
                else Pending!.Result.SetResult(phase == SessionConnectionPhase.Failed ? ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Failed("I/O", "连接中断 — bağlantı échec")
                    : ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(phase == SessionConnectionPhase.Cancelled)));
                await work!;
            }
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "bob"; break;
                case "colony": Ports.Colony = "other-colony"; break;
                case "server": Ports.Server = "http://other"; break;
                case "auth": Ports.Auth = "other-token"; break;
                case "login": Login(); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "stop": Connection.Stop(false); break;
                case "diagnostic": _ = Connection.Start(Ports.ReadContext(), null, 0, 0); break;
                case "snapshot": Ports.Snapshot = "next"; break;
                case "password": Ports.Password = "changed"; break;
                case "steam": Ports.SteamTicket = "changed"; break;
                case "game": Ports.GameOwner = new(); break;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Connection.Stop(false);
            if (work is null) return;
            // Also release a transport call entering after Stop's ownership check.
            lock (cleanupGate)
            {
                closing = true;
                Pending?.Result.TrySetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
            }
            try { await work.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException ex) when (ex.Message == "presentation") { }
        }
    }
    private sealed class View : IClientConnectionStatusPresentation
    {
        internal readonly List<string> Keys = new(), Statuses = new();
        internal readonly List<(string Text, bool Warning)> Diagnostics = new();
        internal (string Name, object? Value)[] Args = Array.Empty<(string, object?)>();
        internal Action? OnText, OnStatus, OnDiagnose;
        public string Text(string key, params (string Name, object? Value)[] args)
        { Keys.Add(key); Args = args; OnText?.Invoke(); return CultureInfo.CurrentCulture.Name + ":" + key; }
        public void Status(string text) { Statuses.Add(text); OnStatus?.Invoke(); }
        public void Diagnose(string text, bool warning) { Diagnostics.Add((text, warning)); OnDiagnose?.Invoke(); }
        internal void Clear() { Keys.Clear(); Statuses.Clear(); Diagnostics.Clear(); Args = Array.Empty<(string, object?)>(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
