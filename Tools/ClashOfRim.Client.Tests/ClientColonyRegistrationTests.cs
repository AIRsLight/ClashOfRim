using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientColonyRegistrationTests
{
    private static int scenarios;
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; scenarios = 0;
        try
        {
            await PeriodicSites();
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); Signatures();
                foreach (var kind in Enum.GetValues<ClientColonyRegistrationKind>())
                {
                    await Basic(kind); await FailureRetry(kind); await ForcedRefresh(kind); await QueueSemantics(kind); await Replacement(kind); await ReceiptLifetime(kind); await DelayedPublication(kind);
                    foreach (string change in Changes)
                        foreach (string stage in new[] { "extensions", "network", "queued", "project", "marker-enqueue" }) await Ownership(kind, stage, change);
                    foreach (string gate in new[] { "config", "session", "snapshot", "playing", "remote", "world", "objects", "colony-context", "collectors", "suppressed", "mutation", "restore" }) await Admission(kind, gate);
                }
                await SharedPipeline(); await Freeze(); await WorldIdentity(); await Deferred(); await EngineThreadBoundary(); await SiteEvents();
            }
            Console.WriteLine($"  colony registration: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static readonly string[] Changes = { "user", "auth", "server", "colony", "login", "generation", "world-id", "game", "world", "reset", "unavailable", "snapshot" };
    private static async Task PeriodicSites()
    {
        var f = new Fixture(); int captures = 0;
        ModColonyAppearanceDto Appearance() { captures++; return new() { ColorHex = "123abc" }; }
        Task Poll(int ticks) => f.Ports.PumpUntil(f.Registration.PollSitesAsync(ticks, Appearance));
        await Poll(0); await Poll(0); await Poll(9999);
        Check(captures == 1 && f.Calls.Count == 1 && f.CapturedAppearance?.ColorHex == "123abc", "Site polling lost its immediate/10000-tick cadence or appearance.");
        Check(f.Calls[0].Extensions.Length == 0 && f.Projects == 0, "Location polling collected or applied the full extension catalog.");
        await Poll(10000); Check(captures == 2 && f.Calls.Count == 1, "Periodic unchanged sites bypassed accepted-signature deduplication.");
        f.Sites[0].Label = "changed"; await Poll(20000);
        Check(captures == 3 && f.Calls.Count == 2, "Periodic changed sites were skipped.");
        await f.Ports.PumpUntil(f.Registration.RequestAsync(ClientColonyRegistrationKind.Sites, "explicit"));
        Check(f.Calls.Count == 3, "Periodic cadence throttled an explicit registration event.");
        Check(f.Calls[2].Extensions.Length == 1 && f.Projects == 1, "Explicit registration no longer refreshed live extensions after a location-only receipt.");
        await Poll(29999); Check(captures == 3, "Explicit registration reset the polling deadline.");
        f.Sites[0].Label = "failed"; f.Fault = "http"; await Poll(30000); f.Fault = null; await Poll(40000);
        Check(f.Calls.Count == 5, "Periodic failure consumed the changed-site signature."); scenarios++;

        foreach (string change in Changes)
        {
            var owner = new Fixture(); await owner.Ports.PumpUntil(owner.Registration.PollSitesAsync(100, () => null));
            owner.Change(change);
            await owner.Ports.PumpUntil(owner.Registration.PollSitesAsync(101, () => null));
            Check(owner.Calls.Count == (change is "snapshot" or "unavailable" ? 1 : 2), "Site polling deadline crossed/reset the wrong ownership: " + change);
            if (change is "snapshot" or "unavailable") continue;
            var replacing = new Fixture();
            await replacing.Ports.PumpUntil(replacing.Registration.PollSitesAsync(0, () => { replacing.Change(change); return new() { ColorHex = "stale" }; }));
            Check(replacing.Calls.Count == 0, "Periodic appearance read crossed original ownership: " + change);
            await replacing.Run(ClientColonyRegistrationKind.Sites);
            Check(replacing.CapturedAppearance is null, "Rejected periodic capture retained appearance in the next request."); scenarios++;
        }
        foreach (string gate in new[] { "suppressed", "pending", "remote", "playing" })
        {
            var denied = new Fixture(); int reads = 0;
            switch (gate)
            {
                case "suppressed": denied.Registration.Suppressed = true; break;
                case "pending": denied.Mutations.Begin(LocalMutationKind.SnapshotRestore, "pending"); break;
                case "remote": denied.Remote = true; break;
                case "playing": denied.Playing = false; break;
            }
            Task Tick(int ticks) => denied.Ports.PumpUntil(denied.Registration.PollSitesAsync(ticks, () => { reads++; return null; }));
            await Tick(0); denied.Registration.Suppressed = false; denied.Mutations.Reset(); denied.Remote = false; denied.Playing = true;
            await Tick(9999); Check(reads == 0 && denied.Calls.Count == 0, "Denied polling captured preferences or retried every tick: " + gate);
            await Tick(10000); Check(reads == 1 && denied.Calls.Count == 1, "Eligible site polling did not resume: " + gate); scenarios++;
        }
    }
    private static void Signatures()
    {
        var a = Site(); var b = Site(); b.TileLayerId = 7;
        string before = ClientColonyRegistration.Signature(new[] { a, b });
        Check(before.Length == 64, "Registration retained an unbounded serialized payload as its fingerprint.");
        Check(before == ClientColonyRegistration.Signature(new[] { b, a }), "Enumeration order changed fingerprint.");
        b.Appearance!.ColorHex = "different"; Check(before != ClientColonyRegistration.Signature(new[] { a, b }), "Appearance change was missed.");
        a.Label = "x\u001fy"; a.FactionName = "z"; b = Site(); b.Label = "x"; b.FactionName = "y\u001fz";
        Check(ClientColonyRegistration.Signature(new[] { a }) != ClientColonyRegistration.Signature(new[] { b }), "Field delimiter collision suppressed a change.");
        Check(ClientColonyRegistration.Signature(new[] { a }) != ClientColonyRegistration.Signature(new[] { a, a }), "Duplicate entries disappeared."); scenarios++;
    }
    private static async Task Basic(ClientColonyRegistrationKind kind)
    {
        var f = new Fixture(); await f.Run(kind);
        Check(f.Calls.Count == 1 && f.Publication.MarkerQueues == 1 && f.Projects == 1 && !f.Registration.Busy, "Accepted registration did not publish once.");
        var applied = f.Publication.MarkerApplications.Single();
        Check(applied.User == "alice" && applied.Markers.Single().Label == "original", "Accepted markers did not reach the engine port.");
        Check(f.Calls[0].Sites.Length == (kind == ClientColonyRegistrationKind.Sites ? 1 : 0), "Extension mode sent sites.");
        Check(f.ExtensionsContext == ("alice", "colony", "base") && f.Calls[0].Context.AuthToken == "auth", "Captured identity lost.");
        await f.Run(kind); Check(f.Calls.Count == 1, "Accepted unchanged data was resubmitted.");
        f.Extensions[0].PayloadJson = "{\"new\":1.25}"; await f.Run(kind); Check(f.Calls.Count == 2, "Changed extension was skipped.");
        f.Sites.Clear(); f.Extensions.Clear(); await f.Run(kind); Check(f.Calls.Count == 2, "Empty capture sent destructive replacement."); scenarios++;
    }
    private static async Task Admission(ClientColonyRegistrationKind kind, string gate)
    {
        var f = new Fixture();
        switch (gate)
        {
            case "config": f.Ports.User = ""; break;
            case "session": f.Session.ClearIdentity(); break;
            case "snapshot": f.Ports.Snapshot = ""; break;
            case "playing": f.Playing = false; break;
            case "remote": f.Remote = true; break;
            case "world": f.HasWorld = false; break;
            case "objects": f.HasObjects = false; break;
            case "colony-context": f.HasColony = false; break;
            case "collectors": f.HasCollectors = false; break;
            case "suppressed": f.Registration.Suppressed = true; break;
            case "mutation": f.Mutations.Begin(LocalMutationKind.ColonyRelocation, "pending"); break;
            case "restore": f.Mutations.Begin(LocalMutationKind.SnapshotRestore, "pending"); break;
        }
        bool allowed = kind == ClientColonyRegistrationKind.Sites ? gate is "world" or "collectors" : gate is "objects" or "colony-context" or "suppressed" or "mutation";
        Check(f.Registration.CanStart(kind) == allowed, "Admission changed: " + kind + "/" + gate);
        await f.Run(kind); Check((f.Calls.Count != 0) == allowed, "Start bypassed admission."); scenarios++;
    }
    private static async Task Ownership(ClientColonyRegistrationKind kind, string stage, string change)
    {
        var f = new Fixture(); f.Once(stage, () => f.Change(change)); await f.Run(kind);
        bool current = change == "snapshot";
        Check(f.Publication.MarkerQueues == (current || stage == "marker-enqueue" ? 1 : 0), "Stale markers: " + kind + "/" + stage + "/" + change);
        bool projectionCurrent = current || stage == "marker-enqueue" && change == "reset";
        Check(f.Publication.MarkerApplications.Count == (projectionCurrent ? 1 : 0), "Queued projection crossed cache ownership: " + kind + "/" + stage + "/" + change);
        Check(!f.Registration.Busy, "Stale request retained busy state.");
        if (change == "reset")
        {
            int calls = f.Calls.Count; await f.Run(kind);
            Check(f.Calls.Count == calls + 1, "Reset request marked its old signature accepted.");
        }
        scenarios++;
    }
    private static async Task FailureRetry(ClientColonyRegistrationKind kind)
    {
        foreach (string fault in new[] { "http", "null", "missing-result", "rejected", "exception", "cancel", "sites", "extensions", "project", "marker-enqueue" })
        {
            if (kind == ClientColonyRegistrationKind.Extensions && fault == "sites") continue;
            var f = new Fixture(); f.Fault = fault;
            if (fault is "sites" or "extensions" or "project" or "marker-enqueue") f.Once(fault, () => throw new InvalidOperationException("injected"));
            await f.Run(kind);
            Check(!f.Registration.Busy && f.Publication.MarkerApplications.Count == 0, "Failure published or retained busy: " + fault);
            int calls = f.Calls.Count; f.Fault = null; await f.Run(kind);
            Check(f.Calls.Count == calls + 1 && f.Publication.MarkerApplications.Count == 1, "Failure was marked accepted or poisoned retry: " + fault); scenarios++;
        }
    }
    private static async Task ForcedRefresh(ClientColonyRegistrationKind kind)
    {
        var f = new Fixture(); f.Hold = NewSignal();
        Task first = f.Registration.StartAsync(kind); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task forced = f.Registration.RequestAsync(kind, "new data", new() { ColorHex = "latest" }, suppressNotification: true);
        Task forcedAgain = f.Registration.RequestAsync(kind, "coalesced", new() { ColorHex = "latest" });
        await f.Ports.PumpUntil(Task.WhenAll(forced, forcedAgain));
        Check(f.Calls.Count == 1 && f.Registration.Busy, "Forced request overlapped active write.");
        f.Hold.SetResult(); await f.Ports.PumpUntil(first);
        Check(f.Calls.Count == 2 && f.Publication.MarkerQueues == 2 && !f.Registration.Busy, "Forced invalidation lost or did not coalesce.");
        Check(f.Calls[1].Muted == (kind == ClientColonyRegistrationKind.Sites), "One-shot notification suppression lost.");
        if (kind == ClientColonyRegistrationKind.Sites) Check(f.CapturedAppearance?.ColorHex == "latest", "Pending appearance was lost.");
        await f.Run(kind); Check(f.Calls.Count == 2, "Completed force did not update signature."); scenarios++;
    }
    private static async Task QueueSemantics(ClientColonyRegistrationKind kind)
    {
        foreach (bool discard in new[] { false, true })
        {
            var f = new Fixture { DuplicateQueue = true, DiscardQueue = discard };
            Task work = f.Registration.StartAsync(kind); Action queued = await f.Ports.Next();
            Check(f.Registration.Busy && f.Publication.MarkerQueues == 0, "Busy released before main-thread completion."); queued(); await work;
            Check(f.Publication.MarkerQueues == (discard ? 0 : 1), "Duplicate/discarded queue published again.");
            f.DiscardQueue = false; await f.Run(kind); Check(f.Calls.Count == (discard ? 2 : 1), "Discard marked unprojected response accepted."); scenarios++;
        }
    }
    private static async Task Replacement(ClientColonyRegistrationKind kind)
    {
        var f = new Fixture(); f.Hold = NewSignal(); Task old = f.Registration.StartAsync(kind);
        await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Change("game");
        Task replacement = f.Registration.StartAsync(kind); Action queued = await f.Ports.Next();
        f.Hold.SetResult(); await old;
        Check(f.Registration.Busy, "Old finally ended replacement."); queued(); await replacement;
        Check(f.Publication.MarkerQueues == 1 && f.Calls.Count == 2, "Old HTTP published into new world."); scenarios++;
    }
    private static async Task SharedPipeline()
    {
        foreach (bool sitesFirst in new[] { true, false })
        {
            var f = new Fixture(); f.Hold = NewSignal();
            var firstKind = sitesFirst ? ClientColonyRegistrationKind.Sites : ClientColonyRegistrationKind.Extensions;
            var secondKind = sitesFirst ? ClientColonyRegistrationKind.Extensions : ClientColonyRegistrationKind.Sites;
            Task first = f.Registration.StartAsync(firstKind); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Registration.StartAsync(secondKind); Check(f.Calls.Count == 1, "Two modes wrote concurrently.");
            f.Hold.SetResult(); await f.Ports.PumpUntil(first);
            Check(f.Calls.Count == (sitesFirst ? 1 : 2), "Shared extension receipt did not suppress redundant write or skipped sites."); scenarios++;
        }
    }
    private static async Task ReceiptLifetime(ClientColonyRegistrationKind kind)
    {
        foreach (string change in new[] { "login", "game", "world-id", "snapshot", "reset" })
        {
            var f = new Fixture(); await f.Run(kind); f.Change(change); await f.Run(kind);
            Check(f.Calls.Count == (change == "snapshot" ? 1 : 2), "Accepted signature survived the wrong scope: " + change); scenarios++;
        }
    }
    private static async Task Freeze()
    {
        var f = new Fixture(); string source = ClientProtocolJson.Serialize(f.Sites[0]);
        f.Once("extensions", () => { f.Sites[0].Tile = 99; f.Sites[0].Appearance!.ColorHex = "changed"; });
        f.OnQueue = () => { f.Response.WorldConfiguration!.PlayerColonySites[0].Label = "borrowed"; f.Response.WorldMapMarkers!.Markers[0].Label = "borrowed"; };
        f.Publication.ApplyingMarkers = markers => markers.First().Label = "engine changed";
        await f.Run(ClientColonyRegistrationKind.Sites);
        Check(ClientProtocolJson.Serialize(f.Calls[0].Sites[0]) == source && f.Publication.MarkerApplications.Single().Markers.Single().Label == "original"
            && f.Publication.Markers.Snapshot.Single().Label == "original", "Input or response borrowed mutable source.");
        Check(f.Occupied.IsBlocked(10, 0, out var site) && site!.Label == "original", "Catalog was not frozen before queue."); scenarios++;
    }
    private static async Task WorldIdentity()
    {
        var f = new Fixture(); f.Response.WorldConfiguration!.WorldConfigurationId = "  world-b  ";
        await f.Run(ClientColonyRegistrationKind.Sites);
        Check(f.WorldId == "world-b" && f.Publication.MarkerQueues == 1 && !f.Registration.Busy, "Own accepted world identity invalidated its remaining publication.");
        await f.Run(ClientColonyRegistrationKind.Sites); Check(f.Calls.Count == 1, "Accepted world identity lost signature."); scenarios++;
    }
    private static async Task Deferred()
    {
        var f = new Fixture(); f.Registration.Suppressed = true;
        await f.Ports.PumpUntil(f.Registration.RequestAsync(ClientColonyRegistrationKind.Sites, "appearance", suppressNotification: true));
        Check(f.Calls.Count == 0, "Suppression bypassed."); f.Registration.Suppressed = false;
        await f.Run(ClientColonyRegistrationKind.Sites); Check(f.Calls.Count == 1 && f.Calls[0].Muted, "Deferred one-shot notification was consumed without a request."); scenarios++;
    }
    private static async Task EngineThreadBoundary()
    {
        foreach (string? fault in new string?[] { null, "http" })
        {
            var f = new Fixture { Fault = fault };
            f.Once("network", () => f.ReadFacts = false);
            await f.Run(ClientColonyRegistrationKind.Sites);
            Check(f.Publication.MarkerQueues == (fault is null ? 1 : 0), "HTTP completion did not preserve game-thread publication."); scenarios++;
        }
    }
    private static async Task DelayedPublication(ClientColonyRegistrationKind kind)
    {
        foreach (bool newerSource in new[] { false, true })
        {
            var f = new Fixture(); var delay = new ClientWorldPublicationTestPorts.DelayedReply();
            f.BeforeReply = delay.Wait;
            f.Response.WorldConfiguration = ClientWorldPublicationTests.Data("old", f.WorldId);
            var task = f.Registration.StartAsync(kind);
            await delay.Complete(f.Ports, task, () =>
            {
                f.Publication.Appearance.SaveCurrent(ClientWorldPublicationTests.Selection("local"));
                if (newerSource) f.Publication.Flow.Publish(f.Publication.Flow.Capture(), ClientWorldPublicationTests.Data("new", f.WorldId),
                    ClientWorldProjection.Sites, () => true, ("newer-source", ClientWorldPublicationTests.Markers("new")));
            });
            Check(f.Publication.Appearance.Read().ColorHex == (newerSource ? "new" : "local"), "Delayed registration overwrote a newer appearance.");
            Check(!f.Registration.Busy && (!newerSource || f.Publication.Markers.Snapshot.Single().Label == "new"), "Delayed registration replaced newer markers or retained admission.");
            scenarios++;
        }
    }
    private static ModPlayerColonySiteDto Site() => new() { Tile = 10, UserId = "remote", ColonyId = "colony", Label = "original", Appearance = new() { ColorHex = "#123456" } };
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task SiteEvents()
    {
        foreach (string gate in new[] { "suppressed", "irrelevant", "capture-suppressed", "capture-error" })
        {
            var f = new Fixture(); await f.Run(ClientColonyRegistrationKind.Sites); int captures = 0, queues = f.Ports.Queued;
            f.Registration.Suppressed = gate == "suppressed";
            try
            {
                await f.Ports.PumpUntil(f.Registration.NotifyAsync(ClientColonyRegistrationKind.Sites, () =>
                {
                    captures++;
                    if (gate == "irrelevant") return null;
                    if (gate == "capture-error") throw new InvalidOperationException("capture");
                    f.Registration.Suppressed = true; return new("event", null);
                }));
                Check(gate != "capture-error", "Capture failure disappeared.");
            }
            catch (InvalidOperationException ex) when (gate == "capture-error" && ex.Message == "capture") { }
            Check(captures == (gate == "suppressed" ? 0 : 1) && f.Ports.Queued == queues, "Skipped site event queued work: " + gate);
            f.Registration.Suppressed = false; await f.Run(ClientColonyRegistrationKind.Sites);
            Check(f.Calls.Count == 1, "Skipped site event invalidated an accepted registration: " + gate); scenarios++;
        }
        foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "world-id", "game", "world", "reset" })
        {
            var f = new Fixture(); await f.Run(ClientColonyRegistrationKind.Sites); int queues = f.Ports.Queued;
            await f.Ports.PumpUntil(f.Registration.NotifyAsync(ClientColonyRegistrationKind.Sites, () =>
            { f.Change(change); return new("old event", new() { ColorHex = "old-owner" }); }));
            Check(f.Calls.Count == 1 && f.Ports.Queued == queues, "Site capture crossed original scope: " + change);
            await f.Run(ClientColonyRegistrationKind.Sites);
            Check(f.CapturedAppearance is null, "Old site event retained pending appearance for replacement scope: " + change); scenarios++;
        }
        var live = new Fixture(); await live.Run(ClientColonyRegistrationKind.Sites);
        var appearance = new ModColonyAppearanceDto { ColorHex = "123abc" };
        await live.Ports.PumpUntil(live.Registration.NotifyAsync(ClientColonyRegistrationKind.Sites, () => { live.Change("snapshot"); return new("created", appearance); }));
        Check(live.Calls.Count == 2 && live.CapturedAppearance?.ColorHex == "123abc" && !live.Calls[1].Muted
            && live.Calls[1].Context.CurrentSnapshotId == "advanced", "Site event lost forced refresh, appearance or latest snapshot."); scenarios++;
    }
    internal sealed class Fixture : IClientWorldIdentity, IClientWorldConfigurationStore, IClientColonyRegistrationRuntime,
        IClientWorldConfigurationRuntime, IClientOccupiedColonySitesRuntime, IClientColonyRegistrationTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientColonyRegistration Registration;
        internal readonly ClientOccupiedColonySites Occupied;
        internal readonly ClientWorldPublicationTestPorts Publication;
        internal readonly List<ModPlayerColonySiteDto> Sites = new() { Site() };
        internal readonly List<ModWorldConfigurationExtensionDto> Extensions = new() { new() { ProviderId = "provider", Kind = "ideo", SchemaVersion = "1", PayloadJson = "{\"name\":\"文化 İ\"}" } };
        internal readonly List<Call> Calls = new();
        internal readonly TaskCompletionSource Entered = NewSignal();
        internal TaskCompletionSource? Hold;
        internal Func<Task>? BeforeReply;
        internal string? Fault;
        internal bool Playing = true, HasWorld = true, HasObjects = true, HasColony = true, HasCollectors = true, ReadFacts = true, Remote, DuplicateQueue, DiscardQueue;
        internal object World = new();
        public string WorldId { get; private set; } = "world-a";
        internal int Projects;
        internal ModColonyAppearanceDto? CapturedAppearance;
        internal (string User, string Colony, string Snapshot) ExtensionsContext;
        internal Action<string>? Hook;
        internal Action? OnQueue;
        internal readonly ModRegisterPlayerColonySitesResponseDto Response = new()
        {
            Result = new() { Accepted = true }, AcceptedCount = 1,
            WorldConfiguration = new() { WorldConfigurationId = "world-a", PlayerColonySites = new() { Site() } },
            WorldMapMarkers = new() { Markers = new() { new() { Label = "original" } } }
        };
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports)); Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
            var configuration = new ClientWorldConfiguration(Session, this, this, null!, null!);
            Occupied = new(Session, this, this);
            Publication = new(Session, Ports, () => WorldId, () => World, configuration, Occupied);
            Registration = new(Session, this, Mutations, this, this, Publication.Flow);
            Publication.Hook = point => { if (point == "marker-enqueue") Hook?.Invoke(point); };
        }
        internal Task Run(ClientColonyRegistrationKind kind) => Ports.PumpUntil(Registration.StartAsync(kind), _ => { });
        public void Remember(string id) { WorldId = id; Hook?.Invoke("remember"); }
        public IClientColonyRegistrationSession Bind() => new Bound(this, Ports.GameOwner, World);
        IClientWorldConfigurationSession IClientWorldConfigurationRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        IClientOccupiedColonySitesSession IClientOccupiedColonySitesRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        public async Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
            IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions, bool suppressNotification, CancellationToken token)
        {
            Calls.Add(new(context, ClientProtocolJson.Copy(sites.ToArray()), ClientProtocolJson.Copy(extensions!.ToArray()), suppressNotification));
            Entered.TrySetResult(); if (Calls.Count == 1 && Hold is not null) await Hold.Task;
            if (BeforeReply is not null) await BeforeReply();
            Hook?.Invoke("network");
            return Fault switch
            {
                "http" => ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>.Failed("network", "failed"),
                "null" => ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>.Ok(null!),
                "missing-result" => ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>.Ok(new()),
                "rejected" => ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>.Ok(new() { Result = new() { Accepted = false } }),
                "exception" => throw new InvalidOperationException("injected"),
                "cancel" => throw new OperationCanceledException(),
                _ => ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>.Ok(Response)
            };
        }
        internal void Once(string stage, Action action) => Hook = value => { if (value == stage) { Hook = null; action(); } };
        private bool Fact(bool value) { Check(ReadFacts, "HTTP continuation read engine availability outside game publication."); return value; }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break; case "auth": Ports.Auth = "new"; break;
                case "server": Ports.Server = "http://other"; break; case "colony": Ports.Colony = "new"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
                case "generation": ClientSessionWork.Reset(); break; case "world-id": WorldId = "new-world"; break;
                case "game": Ports.GameOwner = new(); break; case "world": World = new(); break;
                case "reset": Registration.Reset(); break; case "unavailable": Session.BeginUnavailable(Ports.Auth, null); break;
                case "snapshot": Ports.Snapshot = "advanced"; break;
            }
        }
        private sealed record Bound(Fixture F, object Game, object World) : IClientColonyRegistrationSession, IClientWorldConfigurationSession, IClientOccupiedColonySitesSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.World);
            public bool Playing => F.Fact(F.Playing); public bool HasWorld => F.Fact(F.HasWorld); public bool HasWorldObjects => F.Fact(F.HasObjects);
            public bool HasColonyContext => F.Fact(F.HasColony); public bool HasExtensionCollectors => F.Fact(F.HasCollectors); public bool HasRemoteSession => F.Fact(F.Remote);
            public IReadOnlyCollection<ModPlayerColonySiteDto> CaptureSites(string user, string colony, ModColonyAppearanceDto? appearance)
            { F.CapturedAppearance = appearance; F.Hook?.Invoke("sites"); return F.Sites; }
            public IReadOnlyCollection<ModWorldConfigurationExtensionDto> CaptureExtensions(string user, string colony, string snapshot)
            { F.ExtensionsContext = (user, colony, snapshot); F.Hook?.Invoke("extensions"); return F.Extensions; }
            public void Enqueue(Action action, Action discard)
            {
                F.OnQueue?.Invoke(); F.Ports.Enqueue(() => { F.ReadFacts = true; F.Hook?.Invoke("queued"); if (F.DiscardQueue) discard(); else action(); if (F.DuplicateQueue) action(); });
            }
            public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyFactions(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyRoads(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyExtensions(ModWorldConfigurationDto configuration, string userId, bool worldState) { F.Projects++; F.Hook?.Invoke("project"); }
            public void ApplyObjectLabels(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public bool CanReserve(int tile, int layer) => tile >= 0 && layer == 0;
            public bool IsNeighborOrSame(int tile, int other, int layer) => tile == other;
            public void EnsureProxy(string user) { }
        }
    }
    internal sealed record Call(ClashOfRimClientNetworkContext Context, ModPlayerColonySiteDto[] Sites, ModWorldConfigurationExtensionDto[] Extensions, bool Muted);
}
