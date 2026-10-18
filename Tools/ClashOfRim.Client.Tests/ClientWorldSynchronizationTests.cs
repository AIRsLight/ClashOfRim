using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldSynchronizationTests
{
    private static int scenarios;
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; scenarios = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (ClientWorldSynchronizationKind kind in Enum.GetValues<ClientWorldSynchronizationKind>())
                {
                    await Success(kind);
                    foreach (string fault in new[] { "http", "missing", "reject", "no-result", "throw", "cancel" }) await Failure(kind, fault);
                    foreach (string change in new[] { "user", "auth", "server", "colony", "login", "world-id", "game", "world", "generation", "unavailable", "reset", "snapshot" })
                        foreach (string stage in new[] { "network", "queue", "summary", "apply" }) await Stale(kind, change, stage);
                    foreach (string denied in new[] { "config", "world-missing", "remote", "session", "snapshot", "restore", "unavailable" }) await Admission(kind, denied);
                    foreach (string stage in new[] { "enqueue", "summary", "apply" }) await StageFailure(kind, stage);
                }
                await Polling(); await Queue(); await Replacement(); await Publication(); await ThreadBoundary(); Signatures();
                await StageFailure(ClientWorldSynchronizationKind.RuntimeObjects, "capture");
                await StageFailure(ClientWorldSynchronizationKind.RuntimeObjects, "tick");
                await StageFailure(ClientWorldSynchronizationKind.CaravanTargets, "capture-trades");
                await StageFailure(ClientWorldSynchronizationKind.CaravanTargets, "trades");
                await SelectiveDiscard();
                await RevertedPending();
            }
            Console.WriteLine($"  world synchronization: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success(ClientWorldSynchronizationKind kind)
    {
        var f = new Fixture(); await f.Run(kind);
        Check(f.Calls.Count == 1 && f.Applied == 1 && !f.Flow.Busy && f.Trades == (kind == ClientWorldSynchronizationKind.CaravanTargets ? 1 : 0), "Success did not complete publication/trade handoff: " + kind);
        Check(f.Calls[0].Context.UserId == "alice" && f.Calls[0].Context.CurrentSnapshotId == "base", "Context was not captured.");
        if (kind == ClientWorldSynchronizationKind.RuntimeObjects)
            Check(f.Calls[0].Objects[0].Label == "original" && f.Calls[0].Objects[0].PathTiles!.SequenceEqual(new[] { 10, 11 }), "Runtime input lost fields."); scenarios++;
    }
    private static async Task Failure(ClientWorldSynchronizationKind kind, string fault)
    {
        var f = new Fixture { Fault = fault }; await f.Run(kind);
        bool accepted = fault == "no-result" || (kind != ClientWorldSynchronizationKind.RuntimeObjects && fault == "reject");
        Check(f.Applied == (accepted ? 1 : 0) && !f.Flow.Busy, "Wrong response acceptance/busy lifetime: " + kind + "/" + fault);
        f.Fault = null; await f.Run(kind); Check(f.Applied == (accepted ? 2 : 1), "Failure poisoned retry."); scenarios++;
    }
    private static async Task Stale(ClientWorldSynchronizationKind kind, string change, string stage)
    {
        var f = new Fixture(); f.Once(stage, () => f.Change(change)); await f.Run(kind);
        Check(f.Applied == (change == "snapshot" || stage == "apply" ? 1 : 0), "Stale request reached game projection: " + kind + "/" + stage + "/" + change);
        Check(f.Trades == (kind == ClientWorldSynchronizationKind.CaravanTargets && change == "snapshot" ? 1 : 0), "Old caravan request refreshed replacement trade state.");
        Check(!f.Flow.Busy, "Old request retained admission."); scenarios++;
    }
    private static async Task Admission(ClientWorldSynchronizationKind kind, string denied)
    {
        var f = new Fixture();
        switch (denied)
        {
            case "config": f.Sites.Ports.User = ""; break; case "world-missing": f.WorldAvailable = false; break;
            case "remote": f.Remote = true; break; case "session": await f.Sites.Session.End(false); break;
            case "snapshot": f.Sites.Ports.Snapshot = ""; break; case "restore": f.Mutations.Begin(LocalMutationKind.SnapshotRestore, "restore"); break;
            case "unavailable": f.Sites.Session.BeginUnavailable("auth", null); break;
        }
        bool allowed = denied is "remote" or "session" ? kind == ClientWorldSynchronizationKind.Markers
            : denied == "snapshot" ? kind != ClientWorldSynchronizationKind.CaravanTargets
            : denied == "restore" ? kind != ClientWorldSynchronizationKind.RuntimeObjects : false;
        // End clears credentials but retains the configured IDs, matching the manual read's existing contract.
        await f.Run(kind); Check(f.Calls.Count == (allowed ? 1 : 0), "Admission changed: " + kind + "/" + denied); scenarios++;
    }
    private static async Task Polling()
    {
        var f = new Fixture { Fault = "http" }; await f.Poll(0); Check(f.Calls.Count == 2, "First poll lost runtime/caravan work.");
        f.Fault = null; await f.Poll(249); Check(f.Calls.Count == 2, "Polling interval ignored.");
        await f.Poll(250); Check(f.Calls.Count == 4 && f.Trades == 1, "Failure consumed signatures before acceptance.");
        await f.Poll(500); Check(f.Calls.Count == 4, "Accepted batch resent before lease expiry.");
        f.Objects[0].Label = "renamed"; await f.Poll(750); Check(f.Calls.Count == 5, "Metadata change was excluded from signature.");
        await f.Poll(3250); Check(f.Calls.Count == 6, "Active runtime lease did not refresh.");
        f.Objects.Clear(); await f.Poll(3500); Check(f.Calls.Count == 7 && f.Calls.Last().Objects.Length == 0, "Last runtime object disappearance did not clear server batch.");
        await f.Poll(6250); Check(f.Calls.Count == 7, "Empty accepted runtime batch kept renewing lease.");
        f.Caravans.Clear(); await f.Poll(6500); f.Caravans.Add(Object()); await f.Poll(6750); Check(f.Trades == 2, "Reappearing caravan did not refresh targets."); scenarios++;
    }
    private static async Task Queue()
    {
        var f = new Fixture { Hold = Signal() }; var old = f.Flow.RequestAsync(ClientWorldSynchronizationKind.Markers, "old"); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 3; i++) await f.Flow.RequestAsync(ClientWorldSynchronizationKind.Markers, "pending" + i);
        await f.Flow.RequestAsync(ClientWorldSynchronizationKind.RuntimeObjects, "runtime");
        f.Objects[0].Label = "after-capture"; f.Hold.SetResult(); await f.Pump(old);
        Check(f.Calls.Count == 3 && f.Calls.Last().Objects[0].Label == "original" && !f.Flow.Busy, "Pending requests were duplicated, borrowed or lost."); scenarios++;
    }
    private static async Task Replacement()
    {
        var f = new Fixture { Hold = Signal() }; var old = f.Flow.RequestAsync(ClientWorldSynchronizationKind.RuntimeObjects, "old"); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var hold = f.Hold; f.Hold = null; f.Change("game");
        await f.Run(ClientWorldSynchronizationKind.RuntimeObjects); Check(f.Applied == 1, "New world waited for old HTTP admission.");
        hold.SetResult(); await f.Pump(old); Check(f.Applied == 1 && !f.Flow.Busy, "Old finally affected replacement request."); scenarios++;
    }
    private static async Task Publication()
    {
        foreach (bool discard in new[] { false, true })
        {
            var f = new Fixture { Duplicate = true, Discard = discard }; await f.Run(ClientWorldSynchronizationKind.RuntimeObjects);
            Check(f.Applied == (discard ? 0 : 1) && !f.Flow.Busy, "Queue completion/consumption changed."); scenarios++;
        }
        var late = new Fixture { Hold = Signal() }; var old = late.Flow.RequestAsync(ClientWorldSynchronizationKind.Markers, "old"); await late.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        late.Markers.Observe(late.Markers.CaptureObservation(), new() { Markers = new() { new() { Label = "newer" } } }, true);
        late.Hold.SetResult(); await late.Pump(old);
        Check(late.Markers.Snapshot.Single().Label == "newer" && late.Applied == 1, "Old HTTP response replaced a newer marker observation."); scenarios++;
    }
    private static async Task ThreadBoundary()
    {
        var f = new Fixture(); f.Once("network", () => f.ReadFacts = false); await f.Run(ClientWorldSynchronizationKind.RuntimeObjects);
        Check(f.Applied == 1, "HTTP completion read engine availability outside game publication."); scenarios++;
    }
    private static async Task StageFailure(ClientWorldSynchronizationKind kind, string stage)
    {
        var f = new Fixture(); f.Once(stage, () => throw new InvalidOperationException("injected")); await f.Run(kind);
        Check(!f.Flow.Busy, "Callback exception retained admission: " + stage);
        int calls = f.Calls.Count; await f.Run(kind); Check(f.Calls.Count == calls + 1, "Callback exception prevented retry: " + stage); scenarios++;
    }
    private static async Task SelectiveDiscard()
    {
        var f = new Fixture { DiscardRemaining = 1 }; f.Objects.Clear();
        await f.Poll(0); Check(f.Applied == 0 && f.Trades == 0, "Discarded initial publication ran effects.");
        await f.Poll(250); Check(f.Calls.Count == 2 && f.Trades == 1, "Discarded first publication acknowledged caravan signature."); scenarios++;
        var retry = new Fixture(); retry.Objects.Clear(); retry.Once("trades", () => throw new InvalidOperationException("trade failure"));
        await retry.Poll(0); await retry.Poll(250);
        Check(retry.Calls.Count == 2 && retry.Trades == 2, "Trade refresh failure consumed caravan change detection."); scenarios++;
    }
    private static async Task RevertedPending()
    {
        foreach (bool reading in new[] { false, true })
        {
            var f = new Fixture(); f.Caravans.Clear(); if (reading) await f.Run(ClientWorldSynchronizationKind.RuntimeObjects);
            f.Hold = Signal(); var work = f.Flow.RequestAsync(reading ? ClientWorldSynchronizationKind.Markers : ClientWorldSynchronizationKind.RuntimeObjects, "held");
            await WaitCalls(f, reading ? 2 : 1);
            f.Objects[0].Label = "transient"; await f.Flow.Poll(250);
            f.Objects[0].Label = "original"; await f.Flow.Poll(500);
            f.Hold.SetResult(); await f.Pump(work);
            Check(f.Calls.Count == (reading ? 2 : 1), "Obsolete pending batch survived a return to accepted/in-flight state."); scenarios++;
        }
        var restore = new Fixture(); restore.Caravans.Clear(); await restore.Run(ClientWorldSynchronizationKind.RuntimeObjects);
        restore.Objects[0].Label = "changed"; restore.Hold = Signal(); var active = restore.Flow.RequestAsync(ClientWorldSynchronizationKind.RuntimeObjects, "held");
        await WaitCalls(restore, 2);
        restore.Objects[0].Label = "original"; await restore.Flow.Poll(250); restore.Hold.SetResult(); await restore.Pump(active);
        Check(restore.Calls.Count == 3 && restore.Calls.Last().Objects[0].Label == "original", "Return to previous accepted state did not follow an in-flight changed write."); scenarios++;
    }
    private static async Task WaitCalls(Fixture fixture, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Calls.Count < count) await Task.Delay(1, timeout.Token);
    }
    private static void Signatures()
    {
        var a = Object(); var b = Object(); b.TileLayerId = 7; b.Label = "b|:雪";
        Check(ClientWorldSynchronization.Signature(new[] { a, b }) == ClientWorldSynchronization.Signature(new[] { b, a })
            && ClientWorldSynchronization.Signature(new[] { a, a }) != ClientWorldSynchronization.Signature(new[] { a }), "Fingerprint lost order independence or duplicates.");
        string before = ClientWorldSynchronization.Signature(new[] { a }); a.PathTiles!.Reverse();
        Check(before != ClientWorldSynchronization.Signature(new[] { a }), "Path order was treated as unordered."); scenarios++;
    }
    private static ModRuntimeWorldObjectMarkerDto Object() => new() { WorldObjectId = "object", Kind = "Caravan", DefName = "Caravan", Tile = 10, Label = "original", PathTiles = new() { 10, 11 } };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed record Call(ClashOfRimClientNetworkContext Context, ModRuntimeWorldObjectMarkerDto[] Objects);
    private sealed class Fixture : IClientWorldSynchronizationRuntime, IClientWorldSynchronizationPresentation, IClientWorldMarkerTransport,
        IClientWorldMarkersRuntime, IClientWorldMarkersPresentation
    {
        internal readonly ClientOccupiedColonySitesTests.Fixture Sites = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientWorldMarkers Markers;
        internal readonly ClientWorldSynchronization Flow;
        internal readonly List<ModRuntimeWorldObjectMarkerDto> Objects = new() { Object() }, Caravans = new() { Object() };
        internal readonly List<Call> Calls = new();
        internal readonly TaskCompletionSource Entered = Signal();
        internal TaskCompletionSource? Hold;
        internal bool WorldAvailable = true, Remote, ReadFacts = true, Duplicate, Discard;
        internal string? Fault;
        internal int Applied, Trades, Ticks, DiscardRemaining;
        internal Action<string>? Hook;
        internal Fixture()
        {
            Sites.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
            Markers = new(Sites.Session, Sites, Sites.Cache, this, this);
            Flow = new(Sites.Session, Sites, Mutations, Markers, this, this, this, () => { Stage("capture-trades"); return () => { Trades++; Stage("trades"); return Task.CompletedTask; }; });
        }
        internal Task Run(ClientWorldSynchronizationKind kind) => Pump(Flow.RequestAsync(kind, "test", "caravans"));
        internal Task Poll(int tick) { Ticks = tick; return Pump(Flow.Poll(tick)); }
        internal Task Pump(Task task) => Sites.Ports.PumpUntil(task, _ => { });
        internal void Change(string kind) { if (kind == "reset") Flow.Reset(); else Sites.Change(kind); }
        internal void Once(string stage, Action action) => Hook = value => { if (stage == value) { Hook = null; action(); } };
        private void Stage(string stage) => Hook?.Invoke(stage);
        public IClientWorldSynchronizationSession Bind() => new Bound(this, Sites.Ports.GameOwner, Sites.World);
        IClientWorldMarkersSession IClientWorldMarkersRuntime.Bind() => new Bound(this, Sites.Ports.GameOwner, Sites.World);
        public string Text(string key, params (string Name, object? Value)[] args) { Stage("text"); return key; }
        public void Status(string text) => Stage("status");
        public void Diagnose(string text) => Stage("diagnostic");
        public void Diagnose(string text, bool warning) => Stage("diagnostic");
        public string Summary(IReadOnlyCollection<ModWorldMapMarkerDto> markers) { Stage("summary"); return "summary"; }
        private async Task Network(ClashOfRimClientNetworkContext context, ModRuntimeWorldObjectMarkerDto[] objects)
        { Calls.Add(new(context, ClientProtocolJson.Copy(objects))); var hold = Hold; Stage("network"); Entered.TrySetResult(); if (hold is not null) await hold.Task; if (Fault == "throw") throw new InvalidOperationException("network"); if (Fault == "cancel") throw new OperationCanceledException(); }
        private ModWorldMapMarkerDeliveryDto Delivery() => new() { Markers = new() { new() { Label = "original" } } };
        public async Task<ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        { await Network(context, Array.Empty<ModRuntimeWorldObjectMarkerDto>()); return Fault == "http" ? ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>.Failed("http", "failed") : ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>.Ok(Fault == "missing" ? null! : Delivery()); }
        public async Task<ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>> WriteAsync(ClashOfRimClientNetworkContext context, IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> objects, CancellationToken token)
        { await Network(context, objects.ToArray()); return Fault == "http" ? ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>.Failed("http", "failed") : ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>.Ok(Fault == "missing" ? null! : new() { Result = Fault == "no-result" ? null : new() { Accepted = Fault != "reject" }, WorldMapMarkers = Delivery() }); }
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldSynchronizationSession, IClientWorldMarkersSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Sites.Ports.GameOwner) && ReferenceEquals(World, F.Sites.World);
            private void Facts() { if (!F.ReadFacts) throw new InvalidOperationException("Worker read engine facts."); }
            public bool HasWorldObjects { get { Facts(); F.Stage("available"); return F.WorldAvailable; } }
            public bool HasRemoteSession { get { Facts(); return F.Remote; } }
            public int Tick { get { Facts(); F.Stage("tick"); return F.Ticks; } }
            public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureObjects() { Facts(); F.Stage("capture"); return F.Objects; }
            public IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> CaptureCaravans() { Facts(); return F.Caravans; }
            public void Enqueue(Action action, Action discard)
            { F.Stage("enqueue"); F.Stage("queue"); F.Sites.Ports.Enqueue(() => { F.ReadFacts = true; if (F.Discard || F.DiscardRemaining-- > 0) discard(); else { action(); if (F.Duplicate) action(); } }); }
            public ClientWorldMarkersApplied Apply(IReadOnlyCollection<ModWorldMapMarkerDto> markers, string user) { Facts(); F.Applied++; F.Stage("apply"); return new(true, "applied"); }
        }
    }
}
