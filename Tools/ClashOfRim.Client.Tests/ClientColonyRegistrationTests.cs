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
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); Signatures();
                foreach (var kind in Enum.GetValues<ClientColonyRegistrationKind>())
                {
                    await Basic(kind); await FailureRetry(kind); await ForcedRefresh(kind); await QueueSemantics(kind); await Replacement(kind); await ReceiptLifetime(kind);
                    foreach (string change in Changes)
                        foreach (string stage in new[] { "extensions", "network", "queued", "project", "markers" }) await Ownership(kind, stage, change);
                    foreach (string gate in new[] { "config", "session", "snapshot", "playing", "remote", "world", "objects", "colony-context", "collectors", "suppressed", "mutation", "restore" }) await Admission(kind, gate);
                }
                await SharedPipeline(); await Freeze(); await WorldIdentity(); await Deferred(); await EngineThreadBoundary();
            }
            Console.WriteLine($"  colony registration: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static readonly string[] Changes = { "user", "auth", "server", "colony", "login", "generation", "world-id", "game", "world", "reset", "unavailable", "snapshot" };
    private static void Signatures()
    {
        var a = Site(); var b = Site(); b.TileLayerId = 7;
        string before = ClientColonyRegistration.Signature(new[] { a, b });
        Check(before == ClientColonyRegistration.Signature(new[] { b, a }), "Enumeration order changed fingerprint.");
        b.Appearance!.ColorHex = "different"; Check(before != ClientColonyRegistration.Signature(new[] { a, b }), "Appearance change was missed.");
        a.Label = "x\u001fy"; a.FactionName = "z"; b = Site(); b.Label = "x"; b.FactionName = "y\u001fz";
        Check(ClientColonyRegistration.Signature(new[] { a }) != ClientColonyRegistration.Signature(new[] { b }), "Field delimiter collision suppressed a change.");
        Check(ClientColonyRegistration.Signature(new[] { a }) != ClientColonyRegistration.Signature(new[] { a, a }), "Duplicate entries disappeared."); scenarios++;
    }
    private static async Task Basic(ClientColonyRegistrationKind kind)
    {
        var f = new Fixture(); await f.Run(kind);
        Check(f.Calls.Count == 1 && f.Markers == 1 && f.Projects == 1 && !f.Registration.Busy, "Accepted registration did not publish once.");
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
        Check(f.Markers == (current || stage == "markers" ? 1 : 0), "Stale markers: " + kind + "/" + stage + "/" + change);
        Check(f.AfterMarkers == (current ? 1 : 0), "Later subscriber crossed ownership: " + kind + "/" + stage + "/" + change);
        Check(!f.Registration.Busy, "Stale request retained busy state."); scenarios++;
    }
    private static async Task FailureRetry(ClientColonyRegistrationKind kind)
    {
        foreach (string fault in new[] { "http", "null", "missing-result", "rejected", "exception", "cancel", "sites", "extensions", "project", "markers" })
        {
            if (kind == ClientColonyRegistrationKind.Extensions && fault == "sites") continue;
            var f = new Fixture(); f.Fault = fault;
            if (fault is "sites" or "extensions" or "project" or "markers") f.Once(fault, () => throw new InvalidOperationException("injected"));
            await f.Run(kind);
            Check(!f.Registration.Busy && f.AfterMarkers == 0, "Failure published or retained busy: " + fault);
            int calls = f.Calls.Count; f.Fault = null; await f.Run(kind);
            Check(f.Calls.Count == calls + 1 && f.AfterMarkers == 1, "Failure was marked accepted or poisoned retry: " + fault); scenarios++;
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
        Check(f.Calls.Count == 2 && f.Markers == 2 && !f.Registration.Busy, "Forced invalidation lost or did not coalesce.");
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
            Check(f.Registration.Busy && f.Markers == 0, "Busy released before main-thread completion."); queued(); await work;
            Check(f.Markers == (discard ? 0 : 1), "Duplicate/discarded queue published again.");
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
        Check(f.Markers == 1 && f.Calls.Count == 2, "Old HTTP published into new world."); scenarios++;
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
        f.Registration.MarkersAccepted += (markers, _) => markers!.Markers[0].Label = "observer";
        await f.Run(ClientColonyRegistrationKind.Sites);
        Check(ClientProtocolJson.Serialize(f.Calls[0].Sites[0]) == source && f.LastMarkerLabel == "original", "Input or response borrowed mutable source.");
        Check(f.Occupied.IsBlocked(10, 0, out var site) && site!.Label == "original", "Catalog was not frozen before queue."); scenarios++;
    }
    private static async Task WorldIdentity()
    {
        var f = new Fixture(); f.Response.WorldConfiguration!.WorldConfigurationId = "  world-b  ";
        await f.Run(ClientColonyRegistrationKind.Sites);
        Check(f.WorldId == "world-b" && f.Markers == 1 && !f.Registration.Busy, "Own accepted world identity invalidated its remaining publication.");
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
            Check(f.Markers == (fault is null ? 1 : 0), "HTTP completion did not preserve game-thread publication."); scenarios++;
        }
    }
    private static ModPlayerColonySiteDto Site() => new() { Tile = 10, UserId = "remote", ColonyId = "colony", Label = "original", Appearance = new() { ColorHex = "#123456" } };
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IClientWorldIdentity, IClientWorldConfigurationStore, IClientColonyRegistrationRuntime,
        IClientWorldConfigurationRuntime, IClientOccupiedColonySitesRuntime, IClientColonyRegistrationTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientColonyRegistration Registration;
        internal readonly ClientOccupiedColonySites Occupied;
        internal readonly List<ModPlayerColonySiteDto> Sites = new() { Site() };
        internal readonly List<ModWorldConfigurationExtensionDto> Extensions = new() { new() { ProviderId = "provider", Kind = "ideo", SchemaVersion = "1", PayloadJson = "{\"name\":\"文化 İ\"}" } };
        internal readonly List<Call> Calls = new();
        internal readonly TaskCompletionSource Entered = NewSignal();
        internal TaskCompletionSource? Hold;
        internal string? Fault;
        internal bool Playing = true, HasWorld = true, HasObjects = true, HasColony = true, HasCollectors = true, ReadFacts = true, Remote, DuplicateQueue, DiscardQueue;
        internal object World = new();
        public string WorldId { get; private set; } = "world-a";
        internal int Markers, AfterMarkers, Projects;
        internal string? LastMarkerLabel;
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
            Occupied = new(Session, this, this, configuration);
            Registration = new(Session, this, Mutations, this, this, configuration, Occupied);
            Registration.MarkersAccepted += (markers, _) => { Markers++; LastMarkerLabel = markers?.Markers.FirstOrDefault()?.Label; Hook?.Invoke("markers"); };
            Registration.MarkersAccepted += (_, _) => AfterMarkers++;
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
    private sealed record Call(ClashOfRimClientNetworkContext Context, ModPlayerColonySiteDto[] Sites, ModWorldConfigurationExtensionDto[] Extensions, bool Muted);
}
