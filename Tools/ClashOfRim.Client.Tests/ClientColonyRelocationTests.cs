using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientColonyRelocationTests
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
                foreach (string mode in new[] { "explicit", "landing", "flight" }) await Success(mode);
                foreach (string fault in new[] { "preflight-http", "preflight-reject", "preflight-missing", "settle", "long", "abandon", "anchor", "upload-http", "upload-throw", "confirm-http", "confirm-reject", "confirm-missing", "publish" })
                    await Failure(fault);
                foreach (string fault in new[] { "preflight-http", "upload-http", "confirm-http" }) await Retry(fault);
                foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "game", "world", "world-id", "snapshot", "unavailable" })
                {
                    await ConfirmationOwnership(change);
                    foreach (string phase in new[] { "preflight", "settle", "long", "upload", "confirm", "markers" }) await Ownership(phase, change);
                }
                await Admission(); await Duplicate(); await FlightOwnership(); await AnchorBeforeRetry(); await ImmediateRetry(); await RevalidateLocal(); await RetryBaseChanged(); await DelayedPublication(); await ProjectionFailure();
            }
            Console.WriteLine($"  colony relocation: {scenarios} scenarios checked");
        }
        catch (Exception ex) { throw new InvalidOperationException($"Relocation scenario {scenarios}: {ex}"); }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task DelayedPublication()
    {
        foreach (bool newerSource in new[] { false, true })
        {
            using var f = new Fixture(); var delay = new ClientWorldPublicationTestPorts.DelayedReply();
            f.BeforeConfirm = delay.Wait; f.AcceptedWorld = ClientWorldPublicationTests.Data("old", f.Cache.WorldId);
            f.Open(); var task = f.ConfirmAction!();
            await delay.Complete(f.Ports, task, () =>
            {
                f.Publication.Appearance.SaveCurrent(ClientWorldPublicationTests.Selection("local"));
                if (newerSource) f.Publication.Flow.Publish(f.Publication.Flow.Capture(), ClientWorldPublicationTests.Data("new", f.Cache.WorldId),
                    ClientWorldProjection.Sites, () => true, ("newer-source", ClientWorldPublicationTests.Markers("new")));
            });
            Check(f.Publication.Appearance.Read().ColorHex == (newerSource ? "new" : "local"), "Delayed relocation overwrote newer preference.");
            Check(f.Completed == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending && !f.Registration.Suppressed, "Superseded relocation view stranded the accepted transaction.");
            if (newerSource) Check(f.Publication.Markers.Snapshot.Single().Label == "new", "Delayed relocation replaced newer markers.");
            scenarios++;
        }
    }
    private static async Task ProjectionFailure()
    {
        using var f = new Fixture();
        f.Publication.Hook = phase => { if (phase == "summary") throw new InvalidOperationException("engine projection"); };
        await f.Run("explicit");
        Check(f.Completed == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending && !f.Registration.Suppressed && f.View.Visible is null,
            "Deferred marker failure changed a successfully completed relocation into a failed transaction."); scenarios++;
    }
    private static async Task Success(string mode)
    {
        using var f = new Fixture(); await f.Run(mode);
        Check(f.Completed == 1 && f.Preflights == 1 && f.Uploads == 1 && f.Confirms == 1 && f.Markers == 1, "Pipeline incomplete: " + mode);
        Check(f.Settles == (mode == "explicit" ? 1 : 0) && f.Abandons == (mode == "explicit" ? 1 : 0), "Implicit move replayed local work.");
        Check(!f.Sync.IsBusy && !f.Mutations.IsPending && !f.Registration.Suppressed && f.View.Visible is null, "Completed relocation retained locks/suppression.");
        Check(f.PreflightContext!.CurrentSnapshotId == "base" && f.ConfirmContext!.CurrentSnapshotId == "moved" && f.Previous == "base" && f.Relocated == "moved"
            && f.PreflightKey == f.ConfirmKey && f.SentTile == 12 && f.SentLayer == 7, "Relocation changed base, key or layered target.");
        Check(f.UploadKind == ModSnapshotUploadKinds.ColonyRelocation && f.Operation == "ColonyRelocation" && f.Ports.CaptureModes.SequenceEqual(new[] { false }), "Snapshot wire/capture policy changed.");
        Check(f.Trace.IndexOf("upload") > (mode == "explicit" ? f.Trace.IndexOf("abandon") : -1) && f.Ports.Published == 1, "Snapshot preceded local movement."); scenarios++;
    }
    private static async Task Failure(string fault)
    {
        using var f = new Fixture { Fault = fault }; await f.Run("explicit");
        bool before = fault.StartsWith("preflight", StringComparison.Ordinal);
        Check(f.Completed == 0 && !f.Sync.IsBusy && f.Mutations.IsPending != before, "Failure released local uncertainty or leaked admission: " + fault);
        Check(f.Settles == (before ? 0 : 1) && f.Registration.Suppressed != before, "Failure lost correct suppression/local stage: " + fault);
        Check((f.View.Retry is not null) == (fault is "upload-http" or "upload-throw" or "confirm-http"), "Unsafe or missing retry: " + fault);
        if (fault is "anchor" or "settle" or "long" or "abandon") Check(f.Uploads == 0, "Invalid/partial local world uploaded.");
        if (fault == "publish") Check(f.Confirms == 0, "Partial snapshot publication confirmed."); scenarios++;
    }
    private static async Task Retry(string fault)
    {
        using var f = new Fixture { Fault = fault }; await f.Run(fault.StartsWith("preflight") ? "landing" : "explicit");
        string key = f.PreflightKey!; Check(f.View.Retry is not null, "Transient failure lacked retry: " + fault);
        int settles = f.Settles, abandons = f.Abandons, uploads = f.Uploads;
        f.Fault = null; var retry = f.View.Retry!; retry(); retry(); await f.Ports.PumpUntil(f.Done.Task, _ => { });
        Check(f.Completed == 1 && f.Settles == settles && f.Abandons == abandons && f.ConfirmKey == key, "Retry replayed game effects or changed relocation identity.");
        Check(f.Uploads == uploads + (fault == "confirm-http" ? 0 : 1) && !f.Sync.IsBusy, "Confirm retry uploaded another snapshot or leaked its lease."); scenarios++;
    }
    private static async Task ConfirmationOwnership(string change)
    {
        using var f = new Fixture(); f.Open(); f.Change(change); await f.Ports.PumpUntil(f.ConfirmAction!(), _ => { });
        Check(f.Preflights == (change == "snapshot" ? 1 : 0), "Old confirmation adopted another owner: " + change); scenarios++;
    }
    private static async Task Ownership(string phase, string change)
    {
        using var f = new Fixture(); f.Once(phase, () => f.Change(change)); await f.Run("explicit");
        Check(f.Completed == 0 && f.Successes == 0 && !f.Sync.IsBusy, "Old continuation completed across " + phase + "/" + change);
        if (phase == "preflight") Check(f.Settles == 0, "Late preflight mutated the game.");
        if (phase is "settle" or "long") Check(f.Abandons == 0, "Old queued settlement abandoned a colony."); scenarios++;
    }
    private static async Task Admission()
    {
        foreach (string mode in new[] { "snapshot", "multiple", "remote", "sync", "mutation", "invalid", "enemies", "colonists", "original" })
        {
            using var f = new Fixture(); f.Open();
            switch (mode)
            {
                case "snapshot": f.Ports.Snapshot = ""; break; case "multiple": f.Homes = 2; break; case "remote": f.Remote = true; break;
                case "sync": f.Sync.TryBeginManual(out _); break; case "mutation": f.Mutations.Begin(LocalMutationKind.Gift, "pending"); break;
                case "invalid": f.Available = false; break; case "enemies": f.TargetFailure = "enemies"; break; case "colonists": f.TargetFailure = "colonists"; break;
                case "original": break;
            }
            if (mode == "original") Check(f.Flow.Command(f.Target(), "terrain")!.Disabled == "terrain", "Original settle rejection lost.");
            else { await f.Ports.PumpUntil(f.ConfirmAction!(), _ => { }); Check(f.Preflights == 0, "Changed admission accepted: " + mode); }
            scenarios++;
        }
    }
    private static async Task Duplicate()
    {
        using var f = new Fixture { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) }; f.Open();
        Task first = f.ConfirmAction!(); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await f.ConfirmAction!();
        Check(!f.Flow.Takeoff(f.Target()) && f.Preflights == 1, "Duplicate confirmation/takeoff started another operation.");
        f.Hold.SetResult(); await f.Ports.PumpUntil(first, _ => { }); Check(f.Completed == 1, "Duplicate disturbed first request."); scenarios++;
    }
    private static async Task FlightOwnership()
    {
        using var f = new Fixture(); Check(f.Flow.Takeoff(f.Target()), "Takeoff refused.");
        Check(f.Sync.IsBusy && f.Mutations.IsPending && f.Registration.Suppressed, "Flight did not protect the old anchor.");
        f.Cache.Change("login"); await f.Ports.PumpUntil(f.Flow.LandAsync(f.Target()), _ => { });
        Check(f.Preflights == 0, "Old flight was confirmed under a replacement login."); scenarios++;
    }
    private static async Task AnchorBeforeRetry()
    {
        using var f = new Fixture { Fault = "upload-http" }; await f.Run("explicit");
        f.Destination = false; f.Fault = null; f.View.Retry!();
        (await f.Ports.Next())(); (await f.Ports.Next())();
        Check(f.Uploads == 1 && f.View.Retry is null && f.Mutations.IsPending, "Retry uploaded an invalid anchor."); scenarios++;
    }
    private static async Task ImmediateRetry()
    {
        using var f = new Fixture { Fault = "confirm-http" };
        f.View.OnShown = () => { f.View.OnShown = null; f.Fault = null; f.View.Retry!(); };
        await f.Run("explicit");
        await f.Ports.PumpUntil(f.Done.Task, _ => { });
        Check(f.Uploads == 1 && f.Confirms == 2 && f.Completed == 1, "Immediate retry was lost or uploaded again."); scenarios++;
    }
    private static async Task RevalidateLocal()
    {
        foreach (string fact in new[] { "target", "homes", "remote", "enemies", "colonists" })
        {
            using var f = new Fixture(); f.Once("preflight", () =>
            {
                if (fact == "target") f.Available = false;
                else if (fact == "homes") f.Homes = 2;
                else if (fact == "remote") f.Remote = true;
                else f.TargetFailure = fact;
            });
            await f.Run("explicit");
            Check(f.Settles == 0 && f.Uploads == 0 && !f.Mutations.IsPending && !f.Sync.IsBusy, "Preflight receipt bypassed changed local facts: " + fact); scenarios++;
        }
        using var unavailable = new Fixture(); unavailable.Cache.Change("unavailable");
        Check(!unavailable.Flow.Takeoff(unavailable.Target()), "Expired session launched an untracked flight."); scenarios++;
    }
    private static async Task RetryBaseChanged()
    {
        using var f = new Fixture { Fault = "confirm-http" }; await f.Run("explicit");
        f.Ports.Snapshot = "other"; f.View.Retry!();
        (await f.Ports.Next())();
        Check(f.Confirms == 1 && f.View.Retry is null && f.View.Message!.Contains("BaseSnapshotChanged"), "Retry lost its base-change explanation or confirmed another snapshot."); scenarios++;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable, IClientWorldCatalogRuntime, IClientColonyRegistrationRuntime, IClientColonyRegistrationTransport,
        IClientColonyRelocationTransport, IClientColonyRelocationPresentation, IClientSnapshotTransport
    {
        internal readonly ClientOccupiedColonySitesTests.Fixture Cache = new();
        internal EventModuleTestPorts Ports => Cache.Ports;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientColonyRegistration Registration;
        internal readonly ClientWorldPublicationTestPorts Publication;
        internal readonly EventModuleFailureView View;
        internal readonly ClientColonyRelocation Flow;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Hold;
        internal readonly List<string> Trace = new();
        internal Func<Task>? ConfirmAction, BeforeConfirm;
        internal ModWorldConfigurationDto? AcceptedWorld;
        internal string? Fault, TargetFailure, Previous, Relocated, PreflightKey, ConfirmKey, UploadKind, Operation;
        internal ClashOfRimClientNetworkContext? PreflightContext, ConfirmContext;
        internal int Homes = 1, Preflights, Uploads, Confirms, Settles, Abandons, Completed, Markers, Successes, SentTile, SentLayer;
        internal bool Remote, Available = true, Destination, Facts = true;
        internal Action<string>? Hook;
        internal Fixture()
        {
            Cache.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "session" });
            var configuration = new ClientWorldConfiguration(Cache.Session, Cache, Cache, null!, null!);
            Publication = new(Cache.Session, Ports, () => Cache.WorldId, () => Cache.World, configuration, Cache.Cache);
            Registration = new(Cache.Session, Cache, Mutations, this, this, Publication.Flow);
            View = new(Ports);
            var transactions = new ClientSnapshotTransaction(Cache.Session, Ports, new(Ports, Ports));
            var recovery = new ClientSnapshotFailureRecovery(Cache.Session, Mutations, Sync, Ports, View);
            Flow = new(Cache.Session, Cache, this, new(Cache.Session, Sync, Mutations, transactions), Sync, Mutations, transactions,
                new(Ports, Ports, this, Sync), recovery, Registration, Publication.Flow, this, this);
            Flow.MarkersAccepted += _ => { Markers++; Stage("markers"); };
            Flow.Completed += () => { Check(!Sync.IsBusy, "Completion notified other domains before admission release."); Completed++; Done.TrySetResult(); };
            Ports.BeforeGameAction = () => Facts = true;
            Ports.OnCapture = () => Stage("capture");
            Ports.OnPublish = () => { Stage("publish"); if (Fault == "publish") throw new IOException("partial persist"); };
        }
        internal IClientColonyRelocationTarget Target() => new TargetPort(this, Ports.GameOwner, Cache.World);
        internal void Open() => Flow.Command(Target(), null)!.Open();
        internal Task Run(string mode)
        {
            if (mode == "explicit") { Open(); return Ports.PumpUntil(ConfirmAction!(), _ => { }); }
            Destination = true;
            if (mode == "flight") Check(Flow.Takeoff(Target()), "Takeoff failed.");
            return Ports.PumpUntil(Flow.LandAsync(Target()), _ => { });
        }
        internal void Change(string change)
        {
            Cache.Change(change);
            if (change == "generation") { Sync.Reset(); Mutations.Reset(); }
        }
        internal void Once(string phase, Action action) => Hook = value => { if (value == phase) { Hook = null; action(); } };
        internal void Stage(string value) { Trace.Add(value); Hook?.Invoke(value); }
        private bool Fact(bool value) { Check(Facts, "Background continuation read engine facts."); return value; }
        public async Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> PreflightAsync(ClashOfRimClientNetworkContext context, int tile, string key, int layer, CancellationToken token)
        {
            Preflights++; PreflightContext = context; PreflightKey = key; SentTile = tile; SentLayer = layer; Entered.TrySetResult();
            if (Hold is not null) await Hold.Task; Stage("preflight"); Facts = false; return Response("preflight");
        }
        public async Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> ConfirmAsync(ClashOfRimClientNetworkContext context, string previous, string relocated, int tile, string key, int layer, CancellationToken token)
        {
            Confirms++; ConfirmContext = context; Previous = previous; Relocated = relocated; ConfirmKey = key; SentTile = tile; SentLayer = layer;
            Stage("confirm"); Facts = false; if (BeforeConfirm is not null) await BeforeConfirm(); return Response("confirm");
        }
        private ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto> Response(string stage)
            => Fault == stage + "-http" ? ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>.Ok(new()
                {
                    Result = Fault == stage + "-missing" ? null : new() { Accepted = Fault != stage + "-reject", Message = "original" },
                    WorldConfiguration = AcceptedWorld ?? new() { WorldConfigurationId = "world-a", PlayerColonySites = new() }, WorldMapMarkers = new()
                });
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key,
            ModSnapshotPackageMetadataDto package, byte[] payload, string? operation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Uploads++; Operation = operation; UploadKind = package.SnapshotUploadKind; Stage("upload"); Facts = false;
            if (Fault == "upload-throw") throw new IOException("injected");
            return Task.FromResult(Fault == "upload-http" ? ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = true }, AcceptedSnapshotId = "moved", NextLineageToken = "lineage" }));
        }
        public void Confirm(string colony, int tile, Func<Task> confirm) => ConfirmAction = confirm;
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public void Message(string text, ClientWorldWriteMessage kind) { if (kind == ClientWorldWriteMessage.Success) Successes++; Stage("message"); }
        public void Diagnose(string message) { }
        public string Blocked(LocalMutationState mutation) => mutation.Kind.ToString();
        public string SnapshotFailure(SnapshotUploadFailure failure) => failure.Code + ":" + failure.Message;
        public IClientWorldCatalogSession Bind() => new Bound(this, Ports.GameOwner, Cache.World);
        IClientColonyRegistrationSession IClientColonyRegistrationRuntime.Bind() => new Bound(this, Ports.GameOwner, Cache.World);
        public Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
            IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions, bool suppressNotification, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldCatalogSession, IClientColonyRegistrationSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.Cache.World);
            public bool Playing => true; public bool HasWorld => true; public bool HasWorldObjects => true; public bool HasColonyContext => true; public bool HasExtensionCollectors => false; public bool HasRemoteSession => F.Remote;
            public IReadOnlyCollection<ModPlayerColonySiteDto> CaptureSites(string user, string colony, ModColonyAppearanceDto? appearance) => throw new NotSupportedException();
            public IReadOnlyCollection<ModWorldConfigurationExtensionDto> CaptureExtensions(string user, string colony, string snapshot) => throw new NotSupportedException();
            public void Enqueue(Action action, Action discard) => F.Ports.Enqueue(() => { F.Facts = true; if (IsCurrent) action(); else discard(); });
        }
        private sealed record TargetPort(Fixture F, object Game, object World) : IClientColonyRelocationTarget
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Ports.GameOwner) && ReferenceEquals(World, F.Cache.World);
            public bool IsAvailable => F.Fact(F.Available); public bool HasRemoteSession => F.Fact(F.Remote);
            public int HomeCount => F.Homes; public string OldLabel => "Colony 文化 Î"; public int Tile => 12; public int Layer => 7;
            public string? SettleFailure => F.TargetFailure;
            public void Settle() { F.Settles++; F.Stage("settle"); F.Homes = 2; F.Destination = true; if (F.Fault == "settle") throw new InvalidOperationException("partial settle"); }
            public void EnqueueSettled(Action action, Action<Exception> failed)
            {
                Check(F.Facts, "Long event was scheduled outside game publication.");
                F.Ports.Enqueue(() => { F.Facts = true; F.Stage("long"); if (F.Fault == "long") failed(new InvalidOperationException("generation failed")); else action(); });
            }
            public void AbandonOld() { F.Abandons++; F.Stage("abandon"); F.Homes = 1; if (F.Fault == "abandon") throw new InvalidOperationException("partial abandon"); }
            public bool IsSingleDestination => F.Fact(F.Destination && F.Homes == 1 && F.Fault != "anchor");
        }
    }
}
