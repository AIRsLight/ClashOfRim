using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Trades;

internal static class ClientTradeOperationsTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (var kind in Enum.GetValues<ClientTradeOperation>())
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var f = new Fixture(kind);
                int completed = 0;
                f.Operations.Completed += (operation, step) => { Check(operation == kind && !f.Sync.IsBusy && !f.Mutations.IsPending, "Completion preceded cleanup."); completed++; };
                Task work = await Begin(f);
                Check(f.Applied == 1 && f.Ports.Captures == 1 && f.Sync.SnapshotInProgress, "Local preparation escaped admission.");
                f.Order.EventId = "changed"; f.Order.OfferedThings[0].Metadata!["state"] = "changed";
                f.Order.OfferedThings[0].PawnPackage!.PendingPreviewPng![0] = 99;
                f.Delivered[0].StackCount = 99; f.Delivered[0].ThingPackage!.Label = "changed";
                f.Delivered[0].PawnPackage!.PendingPreviewPng![0] = 99;
                (await f.Ports.Next())(); await work;
                Check(f.Sent!.Order.EventId == "order" && f.Sent.Order.OfferedThings[0].Metadata!["state"] == "original"
                    && f.Sent.Order.OfferedThings[0].PawnPackage!.PendingPreviewPng![0] == 7
                    && f.Sent.Delivered[0].StackCount == 5 && f.Sent.Delivered[0].ThingPackage!.Label == "original"
                    && f.Sent.Delivered[0].PawnPackage!.PendingPreviewPng![0] == 7
                    && f.Context!.CurrentSnapshotId == "base" && completed == 1 && f.Ports.Snapshot == "accepted", "Trade borrowed mutable source state or failed publication.");
                Check(f.State.CopyOrders().Single().Status == (kind == ClientTradeOperation.Create ? "Recorded" : "AppliedToSnapshot"), "Fulfillment did not update market state.");
            }
            foreach (var kind in Enum.GetValues<ClientTradeOperation>())
            {
                await CompletionReleaseReplacement(kind);
                await Retry(kind, build: true); await Retry(kind, build: false);
                foreach (bool exception in new[] { false, true })
                foreach (bool mutated in new[] { false, true })
                {
                    var f = new Fixture(kind) { ApplyFailure = !exception, ApplyException = exception, SignalMutation = mutated };
                    await (await Begin(f));
                    if (mutated) (await f.Ports.Next())();
                    Check(f.Mutations.IsPending == mutated && f.Submissions == 0 && f.Ports.Captures == 0 && !f.Sync.IsBusy
                        && f.Failure.Retry is null, "Local failure lost partial effects or offered a repeated removal.");
                }
                foreach (string failure in new[] { "rejected", "missing-event", "missing-snapshot", "package-transient", "package-rejected" })
                {
                    var f = new Fixture(kind) { FailureMode = failure };
                    Task work = await Begin(f);
                    if (failure.StartsWith("missing", StringComparison.Ordinal)) (await f.Ports.Next())();
                    await work; (await f.Ports.Next())();
                    Check(f.Ports.Published == 0 && f.Mutations.IsPending && !f.Sync.IsBusy
                        && (f.Failure.Retry is not null) == (failure == "package-transient"), "Failure recovery misclassified " + failure);
                }
                var publication = new Fixture(kind);
                publication.Ports.OnPublish = () => throw new InvalidOperationException("partial publication");
                Task publishing = await Begin(publication);
                try { (await publication.Ports.Next())(); } catch (InvalidOperationException) { }
                await publishing; (await publication.Ports.Next())();
                Check(publication.Ports.Published == 1 && publication.Mutations.IsPending && publication.Failure.Retry is null,
                    "Partial publication cleared recovery or allowed resubmission.");
                foreach (string change in new[] { "snapshot", "server", "user", "auth", "colony", "game", "login", "generation" })
                {
                    var f = new Fixture(kind) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                    Task work = await Begin(f); await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Change(f, change); f.Sync.Reset(); Check(f.Sync.TryBeginManual(out var replacement), "Replacement admission failed.");
                    f.Hold.SetResult(f.Success()); await work;
                    Check(f.Ports.Published == 0 && replacement!.IsCurrent, "Late trade published or released a new lease: " + change);
                    replacement!.Dispose();
                    var queued = new Fixture(kind); work = await Begin(queued);
                    var publish = await queued.Ports.Next(); Change(queued, change); publish(); await work;
                    Check(queued.Ports.Published == 0, "Queued publication crossed ownership: " + change);
                    if (kind == ClientTradeOperation.SelfDelivery)
                    {
                        var hydration = new Fixture(kind); work = hydration.Start();
                        var apply = await hydration.Ports.Next(); Change(hydration, change); apply(); await work;
                        Check(hydration.Applied == 0 && hydration.Ports.Captures == 0 && hydration.Submissions == 0, "Hydration callback changed another game or session.");
                    }
                }
                foreach (string rejection in new[] { "empty", "missing-id", "closed", "disabled-mode", "missing-memo", "source", "pending", "busy", "disabled", "unconfigured", "no-snapshot" })
                {
                    if (kind == ClientTradeOperation.Create && rejection is "missing-id" or "closed" or "disabled-mode" or "missing-memo") continue;
                    if (kind != ClientTradeOperation.Create && rejection == "empty") continue;
                    if (kind != ClientTradeOperation.SelfDelivery && rejection == "missing-memo") continue;
                    var f = new Fixture(kind);
                    switch (rejection)
                    {
                        case "empty": f.Order.OfferedThings.Clear(); f.Order.RequestedThings.Clear(); break;
                        case "missing-id": f.Order.EventId = ""; break;
                        case "closed": f.Order.Status = "Cancelled"; break;
                        case "disabled-mode": f.Order.AllowSelfPickup = f.Order.AllowServerDropPod = false; break;
                        case "missing-memo": f.Order.ViewerAcceptedMemoEventId = ""; break;
                        case "source": f.RejectSource = true; break;
                        case "pending": f.Mutations.Begin(LocalMutationKind.Bank, "bank"); break;
                        case "busy": f.Sync.TryBeginManual(out _); break;
                        case "disabled": f.PublishMarket(false); break;
                        case "unconfigured": f.Ports.User = ""; break;
                        case "no-snapshot": f.Ports.Snapshot = ""; break;
                    }
                    await f.Start();
                    Check(f.Applied == 0 && f.Hydrations == 0 && f.Submissions == 0 && f.Ports.Captures == 0, "Rejected trade mutated source: " + rejection);
                    f.Sync.Reset();
                }
                var changedBase = new Fixture(kind) { TransportFailure = true };
                await (await Begin(changedBase)); (await changedBase.Ports.Next())();
                changedBase.Ports.Snapshot = "newer"; changedBase.Failure.Retry!(); (await changedBase.Ports.Next())();
                Check(changedBase.Failure.Retry is null && changedBase.Failure.Exit is not null && changedBase.Submissions == 1, "Retry rebound to another base snapshot.");
            }
            var revalidate = new Fixture(ClientTradeOperation.SelfDelivery);
            Task pending = revalidate.Start(); var continuation = await revalidate.Ports.Next(); revalidate.RejectSource = true; continuation(); await pending;
            Check(revalidate.Applied == 0 && !revalidate.Mutations.IsPending && !revalidate.Sync.IsBusy, "Hydration did not revalidate changed caravan location.");
            var hydrateFailure = new Fixture(ClientTradeOperation.SelfDelivery) { HydrationFailure = true };
            await hydrateFailure.Start();
            Check(hydrateFailure.Applied == 0 && !hydrateFailure.Mutations.IsPending && !hydrateFailure.Sync.IsBusy, "Failed hydration left an unmodified game pending.");
            await Actions();
            Targets();
        }
        finally { CultureInfo.CurrentCulture = original; ClientSessionWork.Reset(); }
    }

    private static async Task<Task> Begin(Fixture f)
    {
        Task task = f.Start();
        if (f.Operation == ClientTradeOperation.SelfDelivery)
        {
            Check(f.Applied == 0 && f.Mutations.IsPending && f.Sync.IsBusy, $"Self delivery preparation invalid: applied={f.Applied}, pending={f.Mutations.IsPending}, busy={f.Sync.IsBusy}, message={f.State.Message}.");
            f.OnGameQueue = true;
            try { (await f.Ports.Next())(); }
            catch (InvalidOperationException) when (f.ApplyException) { }
            finally { f.OnGameQueue = false; }
        }
        return task;
    }

    private static async Task CompletionReleaseReplacement(ClientTradeOperation kind)
    {
        foreach (string change in new[] { "server", "user", "colony", "auth", "snapshot", "game", "login", "generation" })
        {
            var f = new Fixture(kind); int completed = 0;
            f.Operations.Completed += (_, _) => completed++;
            f.Sync.SnapshotChanged += notice => { if (notice.Phase == SnapshotTransactionPhase.Ended) Change(f, change); };
            var work = await Begin(f); (await f.Ports.Next())(); await work;
            Check(completed == 0 && !f.Sync.IsBusy, "Trade completion borrowed the source installed during release: " + kind + "/" + change);
            f.Sync.Reset();
        }
    }
    private static async Task Retry(ClientTradeOperation kind, bool build)
    {
        var f = new Fixture(kind) { TransportFailure = !build };
        if (build) f.Ports.CaptureFailure = new("BuildFailure", "injected", SnapshotUploadMessage.BuildFailed);
        await (await Begin(f)); (await f.Ports.Next())();
        Check(f.Failure.Retry is not null && !f.Sync.IsBusy && f.Mutations.IsPending, "Retryable failure lost pending ownership.");
        var command = f.Sent; var package = f.Package; var payload = f.Payload; var retry = f.Failure.Retry!;
        f.Ports.CaptureFailure = null; f.TransportFailure = false;
        retry(); (await f.Ports.Next())();
        Check(f.Applied == 1 && f.Hydrations == (kind == ClientTradeOperation.SelfDelivery ? 1 : 0)
            && f.Ports.Captures == (build ? 2 : 1) && f.Ports.Published == 1 && !f.Mutations.IsPending,
            "Retry repeated local exchange or failed confirmation.");
        if (!build) Check(ReferenceEquals(command, f.Sent) && ReferenceEquals(package, f.Package) && ReferenceEquals(payload, f.Payload),
            "Retry changed command, idempotency key or snapshot bytes.");
        int submissions = f.Submissions; retry(); Check(f.Submissions == submissions, "Consumed failure window resubmitted.");
    }
    private static void Change(Fixture f, string change)
    {
        switch (change)
        {
            case "snapshot": f.Ports.Snapshot = "newer"; break;
            case "server": f.Ports.Server = "http://other"; break;
            case "user": f.Ports.User = "other"; break;
            case "auth": f.Ports.Auth = "other"; break;
            case "colony": f.Ports.Colony = "other"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new-login" }); break;
            case "generation": ClientSessionWork.Reset(); break;
        }
    }
    private static async Task Actions()
    {
        foreach (string change in new[] { "snapshot", "server", "user", "auth", "colony", "game", "login", "generation" })
        {
            var f = new Fixture(ClientTradeOperation.Create);
            var actions = new ClientTradeActions(f.Operations, f.State, f.Session, f, () => Array.Empty<ModWorldMapMarkerDto>(), () => null, () => null);
            var owner = actions.Capture(); int calls = 0;
            Action callback = actions.Bind(owner, () => calls++);
            actions.Enqueue(owner, () => calls++);
            Change(f, change);
            bool current = change == "snapshot";
            Check(actions.Owns(owner) == current, "Trade UI scope borrowed a new owner or rejected normal snapshot advancement.");
            callback(); (await f.Ports.Next())();
            var quote = await actions.QuoteAsync(owner, f.Order.OfferedThings, f.Order.RequestedThings);
            Check(calls == (current ? 2 : 0) && f.Quotes == (current ? 1 : 0) && quote.Success == current,
                "Obsolete trade callback or quote reached a new session.");
            if (current) Check(f.Context!.CurrentSnapshotId == "newer", "New quote failed to use the current snapshot in its original session.");
        }
    }
    private static void Targets()
    {
        var order = Order(); order.Owner = new() { UserId = "bob", ColonyId = "there" };
        order.TargetContext = new() { MapUniqueId = "map", WorldObjectId = "world", Tile = 4 };
        ModWorldMapMarkerDto Marker(int layer, string map = "map", string world = "world") => new() {
            OwnerUserId = "bob", OwnerColonyId = "there", MapId = map, WorldObjectId = world, Tile = 4, TileLayerId = layer, CanTrade = true };
        var target = ClientTradeTargets.Resolve(order, new[] { Marker(0, "wrong"), Marker(0, world: "wrong"), Marker(2) });
        Check(target == new ClientTradeLocation(4, 2) && ClientTradeTargets.CanSelfDeliver(order, target, 4, 2)
            && !ClientTradeTargets.CanSelfDeliver(order, target, 4, 0), "Trade target lost map/world/layer identity.");
        Check(ClientTradeTargets.Resolve(order, Array.Empty<ModWorldMapMarkerDto>()) == new ClientTradeLocation(4, 0), "Legacy default layer fallback changed.");
        order.Status = "Fulfilled"; Check(!ClientTradeTargets.CanSelfDeliver(order, target, 4, 2), "Closed order remained fulfillable.");
        order.Status = ""; Check(ClientTradeTargets.CanSelfDeliver(order, target, 4, 2), "Legacy empty status lost compatibility.");
        order.ViewerAcceptedMemoEventId = ""; Check(!ClientTradeTargets.CanSelfDeliver(order, target, 4, 2), "Missing memo remained fulfillable.");
    }
    private static ModThingReferenceDto Thing() => new() { GlobalKey = "wood", DefName = "WoodLog", StackCount = 5,
        Metadata = new() { ["state"] = "original" }, ThingPackage = new() { Label = "original" }, PawnPackage = new() { PendingPreviewPng = new byte[] { 7, 8, 9 } } };
    private static ModTradeOrderSummaryDto Order() => new() { EventId = "order", Status = "Recorded", AllowSelfPickup = true, AllowServerDropPod = true,
        ViewerHasAccepted = true, ViewerAcceptedMemoEventId = "memo", OfferedThings = new() { Thing() }, RequestedThings = new() { Thing() } };

    private sealed class Fixture : IClientTradeSource, IClientTradeOperationsTransport, IClientTradeOperationsPresentation, IClientTradeMarketPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly EventModuleFailureView Failure;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientTradeState State;
        internal readonly ClientTradeOperations Operations;
        internal readonly ModTradeOrderSummaryDto Order = ClientTradeOperationsTests.Order();
        internal readonly List<ModThingReferenceDto> Delivered = new() { Thing() };
        internal bool TransportFailure, ApplyFailure, ApplyException, RejectSource, HydrationFailure, OnGameQueue;
        internal bool SignalMutation = true;
        internal string FailureMode = "";
        internal int Applied, Hydrations, Submissions, Quotes;
        internal ClientTradeCommand? Sent;
        internal ClashOfRimClientNetworkContext? Context;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ClientTradeReceipt>>? Hold;
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int gameThread;
        internal Fixture(ClientTradeOperation operation)
        {
            ClientSessionWork.Reset(); Operation = operation; gameThread = Environment.CurrentManagedThreadId;
            Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Failure = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports));
            State = new(Session, Ports, this); PublishMarket(true);
            Operations = new(new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations, transactions,
                new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, Failure), State, this, this);
        }
        internal void PublishMarket(bool enabled)
        { var page = State.BeginPage(State.Capture(), "Open", true)!; State.PublishPage(page, new() { Orders = new() { Order }, TradeMarketplaceEnabled = enabled }); State.EndPage(page); }
        internal Task Start() => Operations.StartAsync(this, Order);
        public ClientTradeOperation Operation { get; }
        public string Key => "caravan";
        public string? Validate(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order) => RejectSource ? "source-rejected" : null;
        public ClientTradeLocalResult Apply(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order, Action mutationStarting)
        {
            Check(Operation == ClientTradeOperation.SelfDelivery ? OnGameQueue : gameThread == Environment.CurrentManagedThreadId, "Local trade left its game thread.");
            Applied++; if (SignalMutation) mutationStarting();
            if (ApplyException) throw new InvalidOperationException("partial local effect");
            return new(!ApplyFailure, Delivered, "applied");
        }
        public Task HydrateAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModThingReferenceDto> things, CancellationToken token)
        { Hydrations++; return HydrationFailure ? Task.FromException(new InvalidOperationException("hydrate")) : Task.CompletedTask; }
        public Task<ClashOfRimClientNetworkResult<ClientTradeReceipt>> SubmitAsync(ClashOfRimClientNetworkContext context, ClientTradeCommand command,
            ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
        {
            Context = context; Sent = command; Package = snapshot; Payload = payload; Submissions++; Submitted.TrySetResult();
            if (FailureMode.StartsWith("package", StringComparison.Ordinal)) throw new ClientProtocolRequestException("upload", "HttpError", FailureMode == "package-transient" ? "503 unavailable" : "403 forbidden");
            return Hold?.Task ?? Task.FromResult(TransportFailure ? ClashOfRimClientNetworkResult<ClientTradeReceipt>.Failed("HttpRequestException", "offline") : Success());
        }
        internal ClashOfRimClientNetworkResult<ClientTradeReceipt> Success() => ClashOfRimClientNetworkResult<ClientTradeReceipt>.Ok(
            new(new() { Accepted = FailureMode != "rejected", ErrorCode = 1 }, FailureMode == "missing-event" ? "" : "exchange",
                FailureMode == "missing-snapshot" ? "" : "accepted", "lineage"));
        public Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModThingReferenceDto> offered,
            IReadOnlyList<ModThingReferenceDto> requested, CancellationToken token)
        { Quotes++; Context = context; return Task.FromResult(ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>.Ok(new())); }
        public string InitialStatus => "initial";
        public string Busy => "busy";
        public string Text(string key, params (string Name, object? Value)[] arguments) => key;
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public void PreparePreviews(IReadOnlyList<ModTradeOrderSummaryDto> orders) { }
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
