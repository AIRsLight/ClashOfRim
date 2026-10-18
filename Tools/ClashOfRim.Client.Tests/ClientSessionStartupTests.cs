using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientSessionStartupTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (var kind in Enum.GetValues<ClientSessionStartKind>())
                {
                    foreach (bool presence in new[] { false, true }) { await Success(kind, presence); count++; }
                    foreach (string gate in new[] { "manual", "snapshot", "configuration", "pending", "no-snapshot", "no-map", "not-playing" })
                    { await Admission(kind, gate); count++; }
                    foreach (string phase in kind == ClientSessionStartKind.Map ? new[] { "login", "upload" } : new[] { "login" })
                    foreach (string fault in new[] { "network", "missing-response", "missing-result", "rejected", "exception", "canceled", "no-token", "no-session", "canceled-after-receipt" })
                    { await Failure(kind, phase, fault); count++; }
                    foreach (string phase in kind == ClientSessionStartKind.Map ? new[] { "logging-text", "login", "received", "identity", "save", "upload", "store", "succeeded-text", "queue", "effect-Events" }
                        : new[] { "logging-text", "login", "received", "identity", "succeeded-text", "queue", "effect-Events" })
                    foreach (string change in new[] { "user", "auth", "server", "colony", "game", "world", "world-id", "login", "work", "reset" })
                    { await Ownership(kind, phase, change); count++; }
                    foreach (string phase in new[] { "login", "received", "identity", "succeeded-text", "queue", "effect-Events" })
                    { await CallbackFailure(kind, phase); count++; }
                    foreach (int index in new[] { 1, 2 }) { await Discard(kind, index); count++; }
                    foreach (string change in new[] { "snapshot", "password", "steam", "expired" })
                    { await SourceBoundary(kind, change); count++; }
                    await ExpiredRecovery(kind); await IdentityReplacement(kind); await Replacement(kind); await FollowupReplacement(kind); await FreshRequest(kind); count += 5;
                }
                await NewerMarkers(); await CaptureFailure(); await PendingDuringLogin(); await CredentialDisposal(); count += 4;
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
        Console.WriteLine($"PASS: session startup ({count} scenarios)");
    }
    private static async Task Success(ClientSessionStartKind kind, bool presence)
    {
        var f = new Fixture(kind) { Presence = presence, RepeatQueue = true, MutateReceipt = true };
        Task work = f.Start(); await f.Start(); await f.Ports.PumpUntil(work);
        bool map = kind == ClientSessionStartKind.Map;
        Check(f.Calls.SequenceEqual(map ? new[] { "login", "upload" } : new[] { "login" }), "Duplicate/reordered startup requests.");
        Check(f.Session.SessionId == "accepted-session" && f.Ports.User == "Alice" && f.Ports.Auth == "accepted-auth", "Frozen canonical credential was not adopted.");
        Check(f.Logouts.IsEmpty && !f.Sync.IsBusy && f.Queues == 1, "Successful startup released a live credential or leaked admission.");
        Check(f.Effects.SequenceEqual(map ? Enum.GetValues<ClientSessionStartEffect>().Where(e => !presence || e != ClientSessionStartEffect.Presence)
            : new[] { ClientSessionStartEffect.Events, ClientSessionStartEffect.Players }), "Wrong startup effects/order.");
        Check(f.WorldPorts.MarkerApplications.Last().Markers.Single().Label == "login", "Frozen login marker receipt was not projected.");
        Check(f.Ports.Captures == (map ? 1 : 0) && f.Ports.Published == (map ? 1 : 0) && f.Messages == (map ? 1 : 0), "Wrong map capture/publication count.");
        if (map) Check(f.UploadKind == ModSnapshotUploadKinds.MapSessionSynchronization && f.Ports.Snapshot == "accepted-snapshot"
            && f.Ports.CaptureModes.SequenceEqual(new[] { false }), "Map startup changed snapshot purpose or capture mode.");
    }
    private static async Task Admission(ClientSessionStartKind kind, string gate)
    {
        var f = new Fixture(kind); ClientSynchronizationCoordinator.Lease? lease = null;
        switch (gate)
        {
            case "manual": Check(f.Sync.TryBeginManual(out lease), "Fixture manual lease."); break;
            case "snapshot": Check(f.Sync.TryAcquireSnapshot(out lease), "Fixture snapshot lease."); break;
            case "configuration": f.Ports.Colony = ""; break;
            case "pending": f.Mutations.Begin(LocalMutationKind.SnapshotRestore, "pending"); break;
            case "no-snapshot": f.Ports.Snapshot = ""; break;
            case "no-map": f.HasMap = false; break;
            case "not-playing": f.Playing = false; break;
        }
        await f.Ports.PumpUntil(f.Start());
        bool allowed = gate == "no-snapshot" && kind == ClientSessionStartKind.Map
            || (gate is "no-map" or "not-playing") && kind == ClientSessionStartKind.Manual;
        Check(f.Calls.IsEmpty != allowed && (f.Effects.Count != 0) == allowed, "Wrong startup admission: " + kind + "/" + gate);
        Check(f.Sync.IsBusy == (lease is not null), "Denied startup consumed a foreign lease."); lease?.Dispose();
    }
    private static async Task Failure(ClientSessionStartKind kind, string phase, string fault)
    {
        var f = new Fixture(kind) { FaultPhase = phase, Fault = fault };
        await f.Ports.PumpUntil(f.Start());
        bool succeeds = phase == "upload" && fault is "missing-result" or "no-token" or "no-session" or "canceled-after-receipt";
        Check(f.Effects.Count == (succeeds ? 5 : 0) && !f.Sync.IsBusy, "Failed stage continued or leaked admission: " + phase + "/" + fault);
        Check(f.Calls.SequenceEqual(phase == "upload" ? new[] { "login", "upload" } : new[] { "login" }), "Failure crossed network stages.");
        Check(f.Logouts.Count == (phase == "login" && fault == "canceled-after-receipt" ? 1 : 0), "Decoded but unadopted credential was lost or live credential released.");
        Check(f.Mismatches == (phase == "login" && fault is "missing-result" or "rejected" or "no-token" or "no-session" ? 1 : 0), "Rejected/malformed login did not reach compatibility presentation.");
        if (!succeeds) Check(f.Statuses.Any(s => s.Contains("Failed") || s.Contains("Rejected") || s.Contains("Exception")), "Failed stage not presented.");
    }
    private static async Task Ownership(ClientSessionStartKind kind, string phase, string change)
    {
        var f = new Fixture(kind); bool changed = false; int calls = 0, effects = 0, queues = 0;
        f.Hook = point =>
        {
            if (point != phase || changed) return;
            changed = true; calls = f.Calls.Count; effects = f.Effects.Count; queues = f.Queues; f.Change(change);
        };
        await f.Ports.PumpUntil(f.Start());
        Check(changed && f.Calls.Count == calls && f.Effects.Count == effects && f.Queues == queues, "Stale startup continued: " + kind + "/" + phase + "/" + change);
        Check(!f.Sync.IsBusy, "Stale startup retained admission.");
        if (phase is "login" or "received") Check(f.Logouts.Count == 1 && f.Logouts.Single().Context.UserId == "Alice"
            && f.Logouts.Single().Context.AuthToken == "accepted-auth" && f.Logouts.Single().Context.ServerBaseUrl == "http://localhost"
            && f.Logouts.Single().Id == "accepted-session", "Stale cleanup borrowed current credentials.");
    }
    private static async Task CallbackFailure(ClientSessionStartKind kind, string phase)
    {
        var f = new Fixture(kind); bool failed = false;
        f.Hook = point => { if (point == phase && !failed) { failed = true; throw new InvalidOperationException("expected"); } };
        await f.Ports.PumpUntil(f.Start(), ex => { if (ex is not InvalidOperationException || ex.Message != "expected") throw ex; });
        Check(failed && !f.Sync.IsBusy && f.Diagnostics.Count == 1, "Callback failure did not finish/report.");
        Check(f.Logouts.Count == (phase == "received" ? 1 : 0), "Observer failure logged out an adopted identity or leaked a credential.");
        Check(f.Effects.Count == (phase == "effect-Events" ? 1 : 0), "Failed callback continued followups.");
    }
    private static async Task Discard(ClientSessionStartKind kind, int index)
    {
        var f = new Fixture(kind) { DiscardIndex = index, RepeatQueue = true };
        await f.Ports.PumpUntil(f.Start());
        Check(!f.Sync.IsBusy && f.Effects.Count == 0 && f.Queues == 0, "Discarded/duplicated callback still published.");
        Check(f.Logouts.Count == (index == 1 ? 1 : 0), "Discarded publication released the wrong credential.");
    }
    private static async Task SourceBoundary(ClientSessionStartKind kind, string change)
    {
        var f = new Fixture(kind); f.Hook = phase => { if (phase == "received") f.Change(change); };
        await f.Ports.PumpUntil(f.Start());
        bool accepted = change == "expired" || change == "snapshot" && kind == ClientSessionStartKind.Manual;
        Check((f.Effects.Count != 0) == accepted && f.Logouts.Count == (accepted ? 0 : 1) && !f.Sync.IsBusy, "Wrong original-context boundary: " + kind + "/" + change);
    }
    private static async Task ExpiredRecovery(ClientSessionStartKind kind)
    {
        var f = new Fixture(kind); f.Change("expired"); await f.Ports.PumpUntil(f.Start());
        Check(!f.Session.IsUnavailable && f.Effects.Count > 0 && f.Logouts.IsEmpty, "Original expired identity could not recover.");
    }
    private static async Task IdentityReplacement(ClientSessionStartKind kind)
    {
        var f = new Fixture(kind); bool changed = false;
        f.Session.IdentityChanged += _ => { if (changed) return; changed = true; f.Change("login"); };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Effects.Count == 0 && f.Logouts.Count == 1 && f.Logouts.Single().Id == "accepted-session" && f.Session.SessionId == "replacement",
            "Startup adopted or cleaned up a reentrant successor identity.");
    }
    private static async Task Replacement(ClientSessionStartKind kind)
    {
        var f = new Fixture(kind); var first = new ClientWorldPublicationTestPorts.DelayedReply(); var second = new ClientWorldPublicationTestPorts.DelayedReply(); int requests = 0;
        f.Before = async phase => { if (phase == "login") await (++requests == 1 ? first : second).Wait(); };
        Task old = f.Start(); Task? successor = null;
        await first.Complete(f.Ports, old, () => { f.Change("work"); successor = f.Start(); });
        Check(f.Sync.IsBusy && !successor!.IsCompleted && f.Logouts.Count == 1, "Old finalizer released the successor admission.");
        await second.Complete(f.Ports, successor!, () => { });
        Check(!f.Sync.IsBusy && f.Effects.Count > 0 && f.Logouts.Count == 1, "Replacement did not finish independently.");
    }
    private static async Task FollowupReplacement(ClientSessionStartKind kind)
    {
        var f = new Fixture(kind); Task? successor = null;
        f.Hook = phase => { if (phase == "effect-Events" && successor is null) successor = f.Start(); };
        await f.Ports.PumpUntil(f.Start()); Check(successor is not null, "Followup was not reached.");
        await f.Ports.PumpUntil(successor!);
        Check(f.Calls.Count(s => s == "login") == 2 && f.Effects.Count(e => e == ClientSessionStartEffect.Players) == 1 && !f.Sync.IsBusy,
            $"Followup restart was blocked or predecessor continued effects: {kind}, calls={string.Join(',', f.Calls)}, effects={string.Join(',', f.Effects)}, busy={f.Sync.IsBusy}, statuses={string.Join(',', f.Statuses)}.");
    }
    private static async Task NewerMarkers()
    {
        var f = new Fixture(ClientSessionStartKind.Map);
        f.Hook = phase => { if (phase == "upload") f.WorldPorts.Markers.Observe(f.WorldPorts.Markers.CaptureObservation(), ClientWorldPublicationTests.Markers("newer"), immediately: true); };
        await f.Ports.PumpUntil(f.Start());
        Check(f.WorldPorts.MarkerApplications.Last().Markers.Single().Label == "newer" && f.Effects.Count == 5, "Delayed login replaced a newer map observation.");
    }
    private static async Task FreshRequest(ClientSessionStartKind kind)
    {
        var f = new Fixture(kind); _ = f.WorldPorts.Markers.CaptureObservation();
        f.Ports.Snapshot = "advanced"; f.Ports.Password = "new-password"; f.Ports.SteamTicket = "new-ticket";
        await f.Ports.PumpUntil(f.Start());
        Check(f.RequestContext?.CurrentSnapshotId == "advanced" && f.RequestContext.OfflinePassword == "new-password"
            && f.RequestContext.SteamAuthTicket == "new-ticket" && f.Effects.Count > 0, "New login borrowed cache creation context.");
    }
    private static async Task CaptureFailure()
    {
        var f = new Fixture(ClientSessionStartKind.Map); f.Ports.CaptureFailure = new("capture", "expected", SnapshotUploadMessage.UploadFailed);
        await f.Ports.PumpUntil(f.Start());
        Check(f.Calls.SequenceEqual(new[] { "login" }) && f.Effects.Count == 0 && f.Logouts.IsEmpty && !f.Sync.IsBusy, "Failed capture uploaded or released an adopted login.");
    }
    private static async Task PendingDuringLogin()
    {
        var f = new Fixture(ClientSessionStartKind.Map); f.Hook = phase => { if (phase == "received") f.Mutations.Begin(LocalMutationKind.SnapshotRestore, "pending"); };
        await f.Ports.PumpUntil(f.Start());
        Check(f.Ports.Captures == 0 && f.Effects.Count == 0 && f.Logouts.Count == 1 && !f.Sync.IsBusy, "Login receipt bypassed unresolved local mutation.");
    }
    private static async Task CredentialDisposal()
    {
        var f = new Fixture(ClientSessionStartKind.Manual); var attempt = new ClientSessionLogin(f.Session, f).Create(f.Ports.ReadContext());
        await attempt.RequestAsync("manual-sync", CancellationToken.None); await attempt.DisposeAsync(); await attempt.DisposeAsync();
        Check(f.Logouts.Count == 1 && attempt.Publish(f.Session.CaptureOwnership()) is null, "Disposed credential published or released twice.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed class Fixture : IClientSessionLoginTransport, IClientSessionLogoutTransport, IClientSnapshotTransport,
        IClientSessionStartupRuntime, IClientSessionStartupPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientWorldPublicationTestPorts WorldPorts;
        internal readonly ClientSessionStartup Module;
        internal readonly ClientSessionStartKind Kind;
        internal readonly ConcurrentQueue<string> Calls = new(), Statuses = new(), Diagnostics = new();
        internal readonly ConcurrentQueue<(ClashOfRimClientNetworkContext Context, string Id)> Logouts = new();
        internal readonly List<ClientSessionStartEffect> Effects = new();
        internal object World = new(); internal bool Playing = true, HasMap = true, Presence, RepeatQueue, MutateReceipt;
        internal string? FaultPhase, Fault, UploadKind; internal int Queues, Messages, Mismatches, DiscardIndex, QueueIndex;
        internal ClashOfRimClientNetworkContext? RequestContext;
        internal Action<string>? Hook; internal Func<string, Task>? Before;
        internal Fixture(ClientSessionStartKind kind)
        {
            ClientSessionWork.Reset(); Kind = kind; Session = new(Ports, this, new(Ports));
            Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "original" });
            WorldPorts = new(Session, Ports, world: () => World);
            Module = new(Session, Sync, Mutations, new(Session, WorldPorts.Markers), new(Session, this), new(Ports, Ports, this, Sync), this, this);
            Session.IdentityChanged += _ => Step("identity");
            Module.QueueAccepted += queue => { Check(queue!.DirectlyProcessable.Single().EventId == "event", "Mutable login queue receipt."); queue.DirectlyProcessable.Clear(); };
            Module.QueueAccepted += queue => { Check(queue!.DirectlyProcessable.Single().EventId == "event", "Queue subscribers shared mutable DTO."); Queues++; Step("queue"); };
            Module.EffectRequested += (_, effect) => { Check(!Sync.IsBusy, "Followup ran under startup admission."); Effects.Add(effect); Step("effect-" + effect); };
            Ports.OnCapture = () => Step("save"); Ports.OnPublish = () => Step("store");
        }
        internal Task Start() => Module.StartAsync(Kind);
        private void Step(string phase) => Hook?.Invoke(phase);
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break; case "colony": Ports.Colony = "other"; break;
                case "game": Ports.GameOwner = new(); break; case "world": World = new(); break;
                case "world-id": WorldPorts.LocalWorldId = "other"; break; case "reset": WorldPorts.Markers.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = "replacement-auth", SessionId = "replacement" }); break;
                case "work": ClientSessionWork.Reset(); Sync.Reset(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break; case "password": Ports.Password = "changed"; break;
                case "steam": Ports.SteamTicket = "changed"; break; case "expired": Session.BeginUnavailable(Ports.Auth, null); break;
            }
        }
        private async Task<string?> Network(string phase)
        { Calls.Enqueue(phase); Step(phase); if (Before is not null) await Before(phase); return phase == FaultPhase ? Fault : null; }
        public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, string purpose, CancellationToken token, Action<ModLoginResponseDto> captureReceipt)
        {
            Check(purpose == (Kind == ClientSessionStartKind.Map ? "map-session" : "manual-sync"), "Wrong login purpose.");
            RequestContext = context;
            string? fault = await Network("login");
            var response = new ModLoginResponseDto { Result = Protocol(fault), AuthenticatedUserId = "  Alice  ", AuthToken = fault == "no-token" ? " " : "accepted-auth",
                SessionId = fault == "no-session" ? "" : "accepted-session", WorldMapMarkers = ClientWorldPublicationTests.Markers("login"),
                EventQueue = new() { DirectlyProcessable = new() { new() { EventId = "event" } } } };
            if (fault is not ("network" or "missing-response" or "exception" or "canceled")) captureReceipt(response);
            Step("received");
            if (fault == "canceled-after-receipt") throw new TaskCanceledException("expected");
            if (MutateReceipt) { response.AuthToken = "mutated"; response.SessionId = "mutated"; response.WorldMapMarkers.Markers[0].Label = "mutated"; response.EventQueue.DirectlyProcessable.Clear(); }
            return Result(fault, response);
        }
        public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string id)
        { Logouts.Enqueue((context, id)); return Task.FromResult(ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new())); }
        public async Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key,
            ModSnapshotPackageMetadataDto package, byte[] payload, string? confirmation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Check(Sync.SnapshotInProgress && confirmation is null && achievements.Count == 0 && context.UserId == "Alice" && context.AuthToken == "accepted-auth", "Snapshot lost admission or accepted identity.");
            UploadKind = package.SnapshotUploadKind; string? fault = await Network("upload");
            return Result(fault, new ModUploadSnapshotResponseDto { Result = Protocol(fault), AcceptedSnapshotId = "accepted-snapshot" });
        }
        private static ModProtocolResponseDto? Protocol(string? fault) => fault == "missing-result" ? null : new() { Accepted = fault != "rejected", ErrorCode = 17, Message = "expected" };
        private static ClashOfRimClientNetworkResult<T> Result<T>(string? fault, T response) => fault switch
        { "network" => ClashOfRimClientNetworkResult<T>.Failed("network", "expected"), "missing-response" => ClashOfRimClientNetworkResult<T>.Ok(default!),
            "exception" => throw new InvalidOperationException("expected"), "canceled" => throw new TaskCanceledException("expected"), _ => ClashOfRimClientNetworkResult<T>.Ok(response) };
        public bool IsPresenceRunning => Presence;
        public IClientSessionStartupSession Bind() => new Bound(this, Ports.GameOwner, World);
        public void Enqueue(Action action, Action discard)
        {
            int index = ++QueueIndex;
            Ports.Enqueue(() => { if (index == DiscardIndex) { discard(); if (RepeatQueue) action(); } else { action(); if (RepeatQueue) action(); } });
        }
        public string Text(string key, params (string Name, object? Value)[] args)
        {
            if (key == "ClashOfRim.Login.StatusLoggingIn") Step("logging-text");
            if (key.EndsWith("StatusSucceeded", StringComparison.Ordinal)) Step("succeeded-text");
            return key + " " + string.Join(";", args.Select(a => a.Value));
        }
        public string Admission(LocalMutationAdmission admission, LocalMutationState? mutation) => admission.ToString();
        public string SnapshotFailure(SnapshotUploadFailure failure) => failure.Message ?? failure.Code;
        public void LoginStatus(string text) => Statuses.Enqueue(text);
        public void SnapshotStatus(string text) => Statuses.Enqueue(text);
        public void Message(string text) => Messages++;
        public void CompatibilityMismatch(ModLoginResponseDto response) => Mismatches++;
        public void Diagnose(string text) => Diagnostics.Enqueue(text);
        private sealed record Bound(Fixture F, object Game, object World) : IClientSessionStartupSession
        {
            public bool IsCurrent => ReferenceEquals(F.Ports.GameOwner, Game) && ReferenceEquals(F.World, World);
            public bool IsPlaying => F.Playing;
            public bool HasMap => F.HasMap;
        }
    }
}
