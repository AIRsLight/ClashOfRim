using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientSessionRecoveryTests
{
    private static readonly string[] IdentityChanges = { "user", "auth", "server", "colony", "login", "work", "game", "world", "world-id" };
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Success(); await Deferred(); await EmptyCredential(); await DuplicateExpiry(); await FrozenReceipt(); await NewerWorldObservation(); count += 6;
                foreach (string change in IdentityChanges.Concat(new[] { "snapshot", "password", "steam" })) { await QueuedExpiry(change); count++; }
                foreach (string stage in new[] { "text-Title", "text-Message", "text-Status", "login-status", "presence-status", "close-tab", "show" })
                foreach (string change in new[] { "login", "game", "work" }) { await ExpiryReentry(stage, change); count++; }
                foreach (string local in new[] { "playing", "menu", "no-game", "no-snapshot", "other-account" }) { await Disconnected(local); count++; }
                foreach (string change in new[] { "none", "login", "game", "work" }) { await Exit(change); count++; }
                foreach (string blocked in new[] { "not-playing", "no-game", "missing", "synchronization", "atomic" }) { await Admission(blocked); count++; }
                foreach (string failure in new[] { "prepare-network", "prepare-rejected", "prepare-snapshot", "login-network", "login-rejected", "login-no-token", "login-no-session", "login-identity", "login-canceled-after-receipt", "metadata-network", "metadata-snapshot", "metadata-throw" })
                { await Rejected(failure); count++; }
                foreach (string stage in new[] { "prepare", "login", "metadata", "queued", "identity", "queue", "ledger", "players", "chat", "presence" })
                foreach (string change in IdentityChanges) { await Ownership(stage, change); count++; }
                foreach (string change in new[] { "snapshot", "atomic", "not-playing", "dialog" }) { await BeforeAcceptance(change); count++; }
                foreach (string stage in new[] { "identity", "text-ReconnectSucceeded", "login-status", "queue", "ledger", "enqueue" }) { await CallbackFailure(stage); count++; }
                foreach (string mode in new[] { "duplicate", "discard", "discard-run" }) { await QueueMode(mode); count++; }
                await Retry(); await ReplacementCleanup(); await LateCallback(); await AdoptedRetry(); count += 4;
            }
            Console.WriteLine($"PASS: session recovery UI workflow ({count} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success()
    {
        var f = new Fixture(); await f.Open();
        Require(f.Session.IsUnavailable && f.Ports.Auth == "" && f.Session.SessionId is null && f.Dialog!.Valid(), "Expiry did not detach its identity before UI.");
        var work = f.Dialog!.Reconnect(); await f.Pump(work);
        Require(f.Ports.Auth == "candidate-token" && f.Session.SessionId == "candidate-session" && !f.Session.IsUnavailable
            && f.Dialog.Results.Single().Success && !f.Dialog.IsOpen && !f.Reconnect.IsRunning && !f.Sync.IsBusy && f.Logout.Calls.Count == 0, "Reconnect failed to adopt/release/close.");
        Require(f.Effects.SequenceEqual(new[] { "queue", "ledger", "players", "chat", "presence", "raid" })
            && f.QueueEvent == "event" && f.RaidEvent == "active-raid" && f.World.MarkerApplications.Single().Markers[0].Label == "login", "Recovery followups lost order or receipt data.");
        Require(f.Transport.Calls.SequenceEqual(new[] { "prepare", "login", "metadata" }) && f.Transport.MetadataContext!.AuthToken == "candidate-token", "Recovery bypassed the existing protocol order.");
        Require(f.Trace.IndexOf("dialog-success") < f.Trace.IndexOf("ledger") && f.LoginText.Contains("ReconnectSucceeded"), "Recovery did not publish status before followups.");
    }
    private static async Task Deferred()
    {
        var f = new Fixture(); var owner = f.Session.CaptureOwnership(); var context = f.Session.ReadContext();
        var hold = f.Session.HoldInvalidation(owner, context)!;
        f.Recovery.Expired("auth", "session");
        Require(f.Ports.Queued == 0 && f.Ports.Auth == "auth" && f.Session.IsUnavailable, "Terminal operation lost deferred identity.");
        hold.Dispose(); (await f.Ports.Next())();
        Require(f.Dialog is not null && f.Ports.Auth == "", "Released invalidation did not present UI.");
    }
    private static async Task EmptyCredential()
    {
        var f = new Fixture();
        foreach (var pair in new[] { ("old", "session"), ("auth", "old"), ("", ""), ((string?)null, (string?)null) }) f.Recovery.Expired(pair.Item1, pair.Item2);
        Require(!f.Session.IsUnavailable && f.Ports.Queued == 0 && f.Ports.Auth == "auth", "Unowned expiry invalidated the current session.");
        f.Playing = false; f.Ports.HasGame = false; await f.Open();
        Require(f.Dialog is not null, "Menu authentication expiry could not use the shared main queue.");
    }
    private static async Task DuplicateExpiry()
    {
        var f = new Fixture(); f.Mode = "duplicate";
        f.Recovery.Expired("auth", "session"); f.Recovery.Expired("auth", "session");
        Require(f.Ports.Queued == 1, "Repeated expiry queued multiple invalidations.");
        (await f.Ports.Next())(); Require(f.Trace.Count(s => s == "show") == 1, "Repeated callback showed multiple expiry dialogs.");
    }
    private static async Task QueuedExpiry(string change)
    {
        var f = new Fixture(); f.Recovery.Expired("auth", "session"); var action = await f.Ports.Next(); f.Change(change); action();
        bool allowed = change is "snapshot" or "password" or "steam" or "world-id";
        Require((f.Dialog is not null) == allowed, "Queued expiry wrong scope: " + change);
        if (change == "login") Require(f.Ports.Auth == "new-token", "Old expiry cleared a replacement login.");
    }
    private static async Task ExpiryReentry(string stage, string change)
    {
        var f = new Fixture(); f.Once(stage, () => f.Change(change)); await f.Open(allowMissing: true);
        Require(f.Dialog is null || !f.Dialog.IsOpen, "Stale expiry kept an interactive dialog: " + stage);
        if (change == "login") Require(f.Ports.Auth == "new-token", "Expiry presentation reentry cleared replacement authentication.");
    }
    private static async Task Disconnected(string local)
    {
        var f = new Fixture(); var work = f.Connection.Start(f.Ports.ReadContext(), "session", 0, 0); var pending = await f.Stream.Next();
        if (local == "menu") f.Playing = false;
        if (local == "no-game") f.Ports.HasGame = false;
        if (local == "no-snapshot") f.Ports.Snapshot = "";
        if (local == "other-account") f.Ports.User = "bob";
        pending.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(false))); await work;
        if (local == "playing") { (await f.Ports.Next())(); Require(f.Title.EndsWith("ConnectionLostTitle") && f.Session.IsUnavailable, "Connection loss used the wrong notice."); }
        else Require(f.Ports.Queued == 0 && !f.Session.IsUnavailable, "Unrelated disconnect launched recovery: " + local);
    }
    private static async Task Exit(string change)
    {
        var f = new Fixture(); await f.Open(); if (change != "none") f.Change(change); f.Dialog!.Exit();
        Require(f.Menus == (change == "none" ? 1 : 0), "Old expiry dialog exited a replacement game/session.");
        if (change != "none") Require(!f.Dialog.IsOpen, "Stale dialog stayed open after rejected interaction.");
    }
    private static async Task Admission(string blocked)
    {
        var f = new Fixture(); await f.Open(); ClientSynchronizationCoordinator.Lease? lease = null;
        switch (blocked)
        {
            case "not-playing": f.Playing = false; break;
            case "no-game": f.Ports.HasGame = false; break;
            case "missing": f.Ports.Snapshot = ""; break;
            case "synchronization": f.Sync.TryBeginManual(out lease); break;
            case "atomic": f.Mutations.Begin(LocalMutationKind.Shop, "pending"); break;
        }
        await f.Dialog!.Reconnect(); lease?.Dispose();
        Require(f.Transport.Calls.Count == 0 && f.Dialog.Results.Count == 1 && !f.Dialog.Results[0].Success, "Blocked reconnect reached network: " + blocked);
    }
    private static async Task Rejected(string scenario)
    {
        var f = new Fixture(scenario); await f.Open(); await f.Pump(f.Dialog!.Reconnect());
        string expected = scenario switch
        {
            "prepare-rejected" or "login-rejected" or "login-no-token" or "login-no-session" => "ReconnectRejected",
            "prepare-snapshot" or "metadata-network" or "metadata-snapshot" => "ReconnectSnapshotChanged",
            "login-identity" => "ReconnectIdentityChanged",
            _ => "ReconnectNetworkFailed"
        };
        Require(f.Dialog.Results.Single().Status.Contains(expected) && !f.Dialog.Results[0].Success && f.Dialog.IsOpen
            && f.Ports.Auth == "" && !f.Reconnect.IsRunning && !f.Sync.IsBusy && f.Effects.Count == 0, "Rejected reconnect result/cleanup changed: " + scenario);
        int releases = scenario.StartsWith("metadata", StringComparison.Ordinal) || scenario is "login-identity" or "login-canceled-after-receipt" ? 1 : 0;
        Require(f.Logout.Calls.Count == releases, "Temporary credential not released: " + scenario);
    }
    private static async Task Ownership(string stage, string change)
    {
        var f = new Fixture(); await f.Open(); bool changed = false;
        void Change() { changed = true; f.Change(change); }
        if (stage is "prepare" or "login" or "metadata") f.Transport.OnStage = point => { if (point == stage) { f.Transport.OnStage = null; Change(); } };
        else f.Once(stage, Change);
        await f.Pump(f.Dialog!.Reconnect());
        Require(changed, "Ownership test did not reach " + stage);
        Require(f.Effects.Count == 0 || (stage is "queue" or "ledger" or "players" or "chat" or "presence") && f.Effects.Last() == stage,
            "Old reconnect continued after " + stage + "/" + change);
        Require(!f.Sync.IsBusy && !f.Reconnect.IsRunning, "Obsolete reconnect retained admission.");
        if (change == "login") Require(f.Ports.Auth == "new-token", "Old reconnect overwrote callback login.");
    }
    private static async Task BeforeAcceptance(string change)
    {
        var f = new Fixture(); await f.Open(); f.Once("queued", () =>
        {
            if (change == "atomic") f.Mutations.Begin(LocalMutationKind.Shop, "pending");
            else if (change == "not-playing") f.Playing = false;
            else if (change == "dialog") f.Dialog!.Close();
            else f.Ports.Snapshot = "new-snapshot";
        });
        await f.Pump(f.Dialog!.Reconnect());
        Require(f.Ports.Auth == "" && f.Effects.Count == 0 && f.Logout.Calls.Count == 1 && !f.Sync.IsBusy, "Changed local state adopted temporary credentials: " + change);
        if (change != "dialog") Require(f.Dialog.Results.Count == 1 && !f.Dialog.Results[0].Success, "Changed local state left reconnect busy.");
    }
    private static async Task CallbackFailure(string stage)
    {
        var f = new Fixture(); await f.Open(); f.Once(stage, () => throw new InvalidOperationException("injected"));
        await f.Pump(f.Dialog!.Reconnect());
        Require(!f.Sync.IsBusy && !f.Reconnect.IsRunning, "Callback exception retained admission: " + stage);
        if (stage != "enqueue") Require(f.Ports.Auth == "candidate-token" && f.Logout.Calls.Count == 0, "Published login was logged out after callback exception: " + stage);
        else Require(f.Ports.Auth == "" && f.Logout.Calls.Count == 1 && f.Effects.Count == 0, "Queued callback ran after its enqueue threw.");
        if (f.Dialog.IsOpen) Require(f.Dialog.Results.Any(r => !r.Success && r.Status.Contains("ReconnectNetworkFailed")), "Callback failure was not reported.");
    }
    private static async Task QueueMode(string mode)
    {
        var f = new Fixture(); await f.Open(); f.Mode = mode; await f.Pump(f.Dialog!.Reconnect());
        Require(!f.Sync.IsBusy && !f.Reconnect.IsRunning, "Queue completion stranded reconnect.");
        if (mode == "duplicate") Require(f.Effects.Count == 6 && f.Dialog.Results.Count == 1 && f.Logout.Calls.Count == 0, "Duplicate game callback repeated recovery.");
        else Require(f.Effects.Count == 0 && f.Dialog.Results.Count == 0 && f.Ports.Auth == "" && f.Logout.Calls.Count == 1, "Discarded callback still adopted recovery.");
    }
    private static async Task FrozenReceipt()
    {
        var f = new Fixture(); await f.Open(); f.Once("queued", () =>
        {
            f.Transport.LastLogin!.AuthToken = "reused"; f.Transport.LastLogin.WorldMapMarkers!.Markers[0].Label = "reused";
            f.Transport.LastMetadata!.SnapshotId = "other"; f.Transport.LastMetadata.ActiveRaidRecovery!.EventId = "reused";
        });
        await f.Pump(f.Dialog!.Reconnect());
        Require(f.Ports.Auth == "candidate-token" && f.RaidEvent == "active-raid" && f.World.MarkerApplications.Single().Markers[0].Label == "login", "Queued reconnect reused mutable transport receipts.");
    }
    private static async Task NewerWorldObservation()
    {
        var f = new Fixture(); await f.Open(); f.Once("identity", () => f.World.Markers.Observe(f.World.Markers.CaptureObservation(), ClientWorldPublicationTests.Markers("newer"), immediately: true));
        await f.Pump(f.Dialog!.Reconnect());
        Require(f.World.MarkerApplications.Last().Markers[0].Label == "newer", "Reconnect borrowed a newer observation number for stale markers.");
    }
    private static async Task Retry()
    {
        var f = new Fixture(); await f.Open(); f.Transport.OnStage = _ => { f.Transport.OnStage = null; throw new InvalidOperationException("injected"); };
        await f.Pump(f.Dialog!.Reconnect()); Require(f.Dialog.IsOpen && f.Logout.Calls.Count == 0, "Failed preparation could not retry.");
        await f.Pump(f.Dialog.Reconnect()); Require(f.Dialog.Results.Count == 2 && f.Dialog.Results[1].Success && f.Effects.Count == 6, "Retry did not acquire fresh admission.");
    }
    private static async Task ReplacementCleanup()
    {
        var f = new Fixture(); await f.Open(); f.Transport.PauseAt = "metadata"; var oldDialog = f.Dialog!; var work = oldDialog.Reconnect();
        await f.Transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Change("login"); f.Recovery.Expired("new-token", "new-session"); (await f.Ports.Next())();
        var successor = f.Dialog!; f.Transport.Continue.TrySetResult(true); await f.Pump(work);
        Require(f.Logout.Calls.Single().Context.AuthToken == "candidate-token" && f.Ports.Auth == "" && successor.Valid(), "Old cleanup invalidated the successor notice.");
        f.Transport.PauseAt = null; await f.Pump(successor.Reconnect()); Require(successor.Results.Single().Success, "Old cleanup prevented a successor attempt.");
    }
    private static async Task AdoptedRetry()
    {
        var f = new Fixture(); await f.Open(); f.Once("identity", () => throw new InvalidOperationException("injected"));
        await f.Pump(f.Dialog!.Reconnect());
        Require(f.Dialog.Valid() && f.Dialog.IsOpen && f.LoginText.Contains("ReconnectNetworkFailed") && f.PresenceText == f.LoginText,
            "A callback failure after adoption stranded the recovery dialog/status.");
        await f.Pump(f.Dialog.Reconnect());
        Require(f.Dialog.Results.Last().Success && f.Effects.Count == 6 && f.Logout.Calls.Count == 0, "Published recovery could not retry safely.");
    }
    private static async Task LateCallback()
    {
        var f = new Fixture(); await f.Open(); f.Mode = "discard"; await f.Pump(f.Dialog!.Reconnect());
        var late = f.LastQueued!; f.Mode = "normal"; await f.Pump(f.Dialog.Reconnect());
        late();
        Require(f.Dialog.Results.Count == 1 && f.Effects.Count == 6 && f.Logout.Calls.Count == 1 && f.Ports.Auth == "candidate-token", "Discarded predecessor republished after a successful retry.");
    }
    private sealed class Fixture : IClientSessionRecoveryRuntime, IClientSessionRecoveryPresentation, IClientSessionRecoveryEffects
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly SessionConnectionTestTransport Stream = new();
        internal readonly SessionLogoutTestTransport Logout = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly SessionReconnectTestTransport Transport;
        internal readonly ClientSessionConnection Connection;
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSessionReconnect Reconnect;
        internal readonly ClientWorldPublicationTestPorts World;
        internal readonly ClientSessionRecovery Recovery;
        internal readonly List<string> Trace = new(), Effects = new();
        internal RecoveryDialog? Dialog;
        internal bool Playing = true;
        internal object WorldOwner = new();
        internal string Mode = "normal", Title = "", LoginText = "", PresenceText = "", QueueEvent = "", RaidEvent = "";
        internal int Menus;
        internal Action<string>? Hook;
        internal Action? LastQueued;
        internal Fixture(string scenario = "success")
        {
            ClientSessionWork.Reset(); Connection = new(Stream); Session = new(Ports, Logout, Connection);
            Session.PublishLogin(new() { Result = new() { Accepted = true }, AuthToken = "auth", SessionId = "session" });
            Session.Ended += Sync.Reset; Session.Ended += Mutations.Reset;
            Transport = new(scenario, "alice", "base");
            Transport.ConfigureLogin = login => { login.WorldMapMarkers = ClientWorldPublicationTests.Markers("login"); login.EventQueue = new() { DirectlyProcessable = new() { new() { EventId = "event" } } }; };
            Reconnect = new(Transport, Session, Sync); World = new(Session, Ports, world: () => WorldOwner);
            Recovery = new(Session, Connection, Reconnect, Mutations, new(Session, World.Markers), this, this, this);
            Session.IdentityChanged += _ => Step("identity");
        }
        internal async Task Open(bool allowMissing = false)
        {
            Recovery.Expired(Ports.Auth, Session.SessionId); (await Ports.Next())();
            if (!allowMissing) Require(Dialog is not null, "No expiry UI was shown.");
            Trace.Clear();
        }
        internal Task Pump(Task work) => Ports.PumpUntil(work, ex => { if (ex.Message != "injected") throw ex; });
        internal void Once(string point, Action action) => Hook = stage => { if (stage != point) return; Hook = null; action(); };
        private void Step(string stage) { Trace.Add(stage); Hook?.Invoke(stage); }
        public IClientSessionRecoverySession Bind() => new Bound(this, Ports.GameOwner, WorldOwner);
        public void Enqueue(Action action, Action discard)
        {
            LastQueued = action;
            Ports.Enqueue(() =>
            {
                Step("queued");
                if (Mode is "discard" or "discard-run") discard();
                if (Mode != "discard") action();
                if (Mode == "duplicate") action();
            });
            Step("enqueue");
        }
        public void ReturnToMenu() { Menus++; _ = Session.End(false); }
        public string Text(string key, params (string Name, object? Value)[] args) { Step("text-" + key.Split('.').Last()); return key + string.Concat(args.Select(x => " " + x.Value)); }
        public void LoginStatus(string text) { LoginText = text; Step("login-status"); }
        public void PresenceStatus(string text) { PresenceText = text; Step("presence-status"); }
        public void CloseMainTab() => Step("close-tab");
        public IClientSessionRecoveryDialog Show(string title, string message, Func<bool> current, Func<IClientSessionRecoveryDialog, Task> reconnect, Action<IClientSessionRecoveryDialog> exit)
        { Title = title; Dialog = new(this, current, reconnect, exit); Step("show"); return Dialog; }
        public void Diagnose(string text, bool warning) => Step(warning ? "diagnose-failed" : "diagnose");
        public void AdoptQueue(ModEventQueueSummaryDto? queue)
        { Require(!Sync.IsBusy, "Followup retained reconnect admission."); QueueEvent = queue!.DirectlyProcessable.Single().EventId; Effect("queue"); }
        public void RefreshLedger(string reason) => Effect("ledger");
        public void RefreshPlayers() => Effect("players");
        public void RefreshChat() => Effect("chat");
        public void StartPresence() => Effect("presence");
        public void ReconcileRaid(ModActiveRaidRecoveryDto? recovery) { RaidEvent = recovery?.EventId ?? ""; Effect("raid"); }
        private void Effect(string kind) { Effects.Add(kind); Step(kind); }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "bob"; break;
                case "auth": Ports.Auth = "other-auth"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "other"; break;
                case "login": Session.PublishLogin(new() { Result = new() { Accepted = true }, AuthToken = "new-token", SessionId = "new-session" }); break;
                case "work": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "game": Ports.GameOwner = new(); break;
                case "world": WorldOwner = new(); break;
                case "world-id": World.LocalWorldId = "other"; break;
                case "snapshot": Ports.Snapshot = "new"; break;
                case "password": Ports.Password = "new"; break;
                case "steam": Ports.SteamTicket = "new"; break;
            }
        }
        private sealed record Bound(Fixture F, object Game, object World) : IClientSessionRecoverySession
        {
            public bool IsCurrent => ReferenceEquals(F.Ports.GameOwner, Game) && ReferenceEquals(F.WorldOwner, World);
            public bool IsPlaying => F.Playing;
            public bool HasGame => F.Ports.HasGame;
        }
        internal sealed class RecoveryDialog : IClientSessionRecoveryDialog
        {
            private readonly Fixture f;
            internal readonly Func<bool> Valid;
            private readonly Func<IClientSessionRecoveryDialog, Task> reconnect;
            private readonly Action<IClientSessionRecoveryDialog> exit;
            internal readonly List<(bool Success, string Status)> Results = new();
            internal RecoveryDialog(Fixture f, Func<bool> valid, Func<IClientSessionRecoveryDialog, Task> reconnect, Action<IClientSessionRecoveryDialog> exit)
            { this.f = f; Valid = valid; this.reconnect = reconnect; this.exit = exit; }
            public bool IsOpen { get; private set; } = true;
            internal Task Reconnect() => reconnect(this);
            internal void Exit() => exit(this);
            public void CompleteReconnect(bool success, string status) { Results.Add((success, status)); if (success) IsOpen = false; f.Step(success ? "dialog-success" : "dialog-failure"); }
            public void Close() { IsOpen = false; f.Step("dialog-close"); }
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
