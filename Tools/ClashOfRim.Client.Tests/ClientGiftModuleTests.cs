using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientGiftModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (ClientGiftSourceKind kind in Enum.GetValues<ClientGiftSourceKind>())
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var f = new Fixture(kind); int completed = 0;
                f.Gifts.Completed += () => { Check(!f.Sync.IsBusy && !f.Mutations.IsPending, "Completion preceded cleanup."); completed++; };
                Task work = f.Start();
                if (kind == ClientGiftSourceKind.TransportPods)
                {
                    Check(f.Captured == 0 && f.Ports.Captures == 0 && f.Mutations.IsPending && f.Sync.IsBusy,
                        "Arrival saved cargo before the engine completed cleanup.");
                    (await f.Ports.Next())();
                }
                Check(f.Captured == 1 && f.Removed == (kind == ClientGiftSourceKind.Caravan ? 1 : 0)
                    && f.Ports.Captures == 1 && f.Sync.IsBusy, "Source capture or removal count is wrong.");
                f.Items[0].StackCount = 900; f.Items[0].Metadata!["state"] = "changed";
                f.Items[0].ThingPackage!.Label = "changed";
                (await f.Ports.Next())(); await work;
                Check(f.Sent!.Things[0].StackCount == 5 && f.Sent.Things[0].Metadata!["state"] == "original"
                    && f.Sent.Things[0].ThingPackage!.Label == "original-package"
                    && f.Context!.CurrentSnapshotId == "base" && completed == 1 && f.Ports.Snapshot == "accepted",
                    "Gift reused mutable UI/source state or failed publication.");
            }
            foreach (ClientGiftSourceKind kind in Enum.GetValues<ClientGiftSourceKind>())
            {
                await RetryAfterFailure(kind, build: true);
                await RetryAfterFailure(kind, build: false);
                foreach (string change in new[] { "snapshot", "server", "auth", "colony", "game", "login", "generation" })
                {
                    var f = new Fixture(kind) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                    Task work = f.Start(); if (kind == ClientGiftSourceKind.TransportPods) (await f.Ports.Next())();
                    await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Change(f, change); f.Sync.Reset(); Check(f.Sync.TryBeginManual(out var replacement), "Replacement lease unavailable.");
                    f.Hold.SetResult(f.Success()); await work;
                    Check(f.Ports.Published == 0 && replacement!.IsCurrent, "Late gift response published or released a new lease: " + change);
                    replacement!.Dispose();
                    var queued = new Fixture(kind); work = queued.Start();
                    if (kind == ClientGiftSourceKind.TransportPods) (await queued.Ports.Next())();
                    var deliver = await queued.Ports.Next(); Change(queued, change); deliver(); await work;
                    Check(queued.Ports.Published == 0, "Queued publication crossed ownership: " + change);
                    if (kind == ClientGiftSourceKind.TransportPods)
                    {
                        var arrival = new Fixture(kind); work = arrival.Start(); var capture = await arrival.Ports.Next();
                        Change(arrival, change); capture(); await work;
                        Check(arrival.Captured == 0 && arrival.Ports.Captures == 0 && arrival.Sent is null,
                            "Delayed arrival captured a replacement game or session: " + change);
                    }
                }
                var rejected = new Fixture(kind) { ServerRejection = true };
                await StartThroughArrival(rejected); (await rejected.Ports.Next())();
                Check(rejected.Failure.Retry is null && rejected.Mutations.IsPending, "Rejected transaction can resend removed cargo.");
                var changedBase = new Fixture(kind) { TransportFailure = true };
                await StartThroughArrival(changedBase); (await changedBase.Ports.Next())();
                changedBase.Ports.Snapshot = "newer"; changedBase.Failure.Retry!(); (await changedBase.Ports.Next())();
                Check(changedBase.Failure.Retry is null && changedBase.Failure.Exit is not null && changedBase.Submissions == 1,
                    "Retry rebound to a different base or lost the exit decision.");
                var publication = new Fixture(kind);
                publication.Ports.OnPublish = () => throw new InvalidOperationException("partial publication");
                Task publishing = publication.Start();
                if (kind == ClientGiftSourceKind.TransportPods) (await publication.Ports.Next())();
                try { (await publication.Ports.Next())(); } catch (InvalidOperationException) { }
                await publishing; (await publication.Ports.Next())();
                Check(publication.Ports.Published == 1 && publication.Mutations.IsPending && publication.Failure.Retry is null,
                    "Partially published gift cleared recovery or could resend.");
            }
            var captureRetry = new Fixture(ClientGiftSourceKind.TransportPods) { CaptureThrowOnce = true };
            await StartThroughArrival(captureRetry); (await captureRetry.Ports.Next())();
            captureRetry.Failure.Retry!(); (await captureRetry.Ports.Next())();
            Check(captureRetry.Captured == 2 && captureRetry.Removed == 0 && captureRetry.Submissions == 1 && captureRetry.Ports.Published == 1,
                "Arrival capture retry re-applied engine removal or failed to finish.");
            foreach (string change in new[] { "snapshot", "server", "auth", "colony", "game", "login", "generation" })
            {
                var f = new Fixture(ClientGiftSourceKind.Caravan);
                var actions = new ClientGiftActions(f.Gifts, f.Session, f.Ports, f, () => f.Capabilities, () => Array.Empty<ModWorldMapMarkerDto>());
                var scope = actions.Capture(); Change(f, change);
                Check(actions.Owns(scope) == (change == "snapshot"), "Gift UI scope borrowed another owner or rejected a normal snapshot advance: " + change);
            }
            foreach (bool exception in new[] { false, true })
            {
                var f = new Fixture(ClientGiftSourceKind.Caravan) { RemovalFailure = !exception, RemovalException = exception };
                await f.Start(); (await f.Ports.Next())();
                Check(f.Mutations.IsPending && f.Removed == 1 && f.Sent is null && !f.Sync.IsBusy && f.Failure.Retry is null,
                    "Partial removal cleared pending state or allowed another removal.");
            }
            foreach (string rejection in new[] { "disabled", "pvp", "target", "tile", "layer", "empty", "policy", "pending", "busy" })
            {
                var f = new Fixture(ClientGiftSourceKind.Caravan);
                if (rejection == "disabled") f.Capabilities = new(false, true);
                if (rejection == "pvp") f.Capabilities = new(true, false);
                if (rejection == "target") f.Target = f.Target with { MapId = "" };
                if (rejection == "tile") f.Target = f.Target with { Tile = 9 };
                if (rejection == "layer") f.Target = f.Target with { TileLayerId = 1 };
                if (rejection == "empty") f.Cargo = f.Cargo with { HasCargo = false };
                if (rejection == "policy") f.CaptureRejected = true;
                if (rejection == "pending") f.Mutations.Begin(LocalMutationKind.Bank, "bank");
                if (rejection == "busy") f.Sync.TryBeginManual(out _);
                await f.Start();
                Check(f.Removed == 0 && f.Submissions == 0 && f.Ports.Captures == 0, "Rejected gift mutated the source: " + rejection);
                f.Sync.Reset();
            }
            Targets();
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static async Task RetryAfterFailure(ClientGiftSourceKind kind, bool build)
    {
        var f = new Fixture(kind) { TransportFailure = !build };
        if (build) f.Ports.CaptureFailure = new("BuildFailure", "injected", SnapshotUploadMessage.BuildFailed);
        await StartThroughArrival(f); (await f.Ports.Next())();
        Check(f.Failure.Retry is not null && !f.Sync.IsBusy && f.Mutations.IsPending, "Retryable failure lost pending ownership.");
        var command = f.Sent; var package = f.Package; var payload = f.Payload; var retry = f.Failure.Retry!;
        f.Ports.CaptureFailure = null; f.TransportFailure = false;
        retry(); (await f.Ports.Next())();
        Check(f.Captured == 1 && f.Removed == (kind == ClientGiftSourceKind.Caravan ? 1 : 0)
            && f.Ports.Captures == (build ? 2 : 1) && f.Ports.Published == 1 && !f.Mutations.IsPending,
            "Gift retry repeated capture/removal or did not confirm authority.");
        if (!build) Check(ReferenceEquals(command, f.Sent) && ReferenceEquals(package, f.Package) && ReferenceEquals(payload, f.Payload),
            "Transport retry rebuilt its committed intent or snapshot.");
        int calls = f.Submissions; retry(); Check(f.Submissions == calls, "Old failure window submitted again.");
    }

    private static async Task StartThroughArrival(Fixture f)
    { Task work = f.Start(); if (f.Cargo.Kind == ClientGiftSourceKind.TransportPods) (await f.Ports.Next())(); await work; }
    private static void Change(Fixture f, string change)
    {
        switch (change)
        {
            case "snapshot": f.Ports.Snapshot = "newer"; break;
            case "server": f.Ports.Server = "http://other"; break;
            case "auth": f.Ports.Auth = "other"; break;
            case "colony": f.Ports.Colony = "other"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new-login" }); break;
            case "generation": ClientSessionWork.Reset(); break;
        }
    }

    private static void Targets()
    {
        var context = new EventModuleTestPorts().ReadContext();
        ModWorldMapMarkerDto Marker(string user, int layer = 0) => new() { Kind = "TradeableColony", OwnerUserId = user,
            OwnerColonyId = "colony", MapId = "map", SnapshotId = "base", Tile = 4, TileLayerId = layer, CanRaid = true };
        var result = ClientGiftTargets.Find(context, new(true, true), new[] { Marker("z"), Marker("a"), Marker("alice"), Marker("other-layer", 1) }, 4, 0, true);
        Check(result.Select(m => m.OwnerUserId).SequenceEqual(new[] { "a", "z" }), "Gift eligibility lost ownership, ordering or layer identity.");
        Check(ClientGiftTargets.Find(context, new(true, false), result, 4, 0, true).Count == 0, "Forced target list bypassed PVP policy.");
    }

    internal sealed class Fixture : IClientGiftSource, IClientGiftTransport, IClientGiftPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly EventModuleFailureView Failure;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientGiftOperations Gifts;
        internal ClientGiftCapabilities Capabilities = new(true, true);
        internal ClientGiftTarget Target = new("bob", "there", "map", "world", 4, 0, "目标");
        internal ClientGiftCargo Cargo;
        internal readonly List<ModThingReferenceDto> Items = new() { new() { GlobalKey = "wood", DefName = "WoodLog", StackCount = 5,
            Metadata = new() { ["state"] = "original" }, ThingPackage = new() { Label = "original-package" } } };
        internal bool TransportFailure, ServerRejection, RemovalFailure, RemovalException, CaptureRejected, CaptureThrowOnce;
        internal int Captured, Removed, Submissions;
        internal ClientGiftCommand? Sent;
        internal ClashOfRimClientNetworkContext? Context;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>>? Hold;
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int gameThread;
        internal Fixture(ClientGiftSourceKind kind)
        {
            ClientSessionWork.Reset(); gameThread = Environment.CurrentManagedThreadId;
            Cargo = new(kind, "cargo", 4, 0, true);
            Session = new ClientSessionLifecycle(Ports, Ports, new ClientSessionConnection(Ports));
            Failure = new EventModuleFailureView(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            Gifts = new ClientGiftOperations(new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations,
                transactions, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, Failure), this, this, () => Capabilities);
        }
        internal Task Start() => Gifts.SendAsync(this, Target, forced: true, "礼物");
        public ClientGiftCargo Describe() => Cargo;
        public ClientGiftCapture Capture(ClashOfRimClientNetworkContext context, bool forced)
        {
            // The caravan path must execute before handing work to the network worker.
            if (Cargo.Kind == ClientGiftSourceKind.Caravan) Check(Environment.CurrentManagedThreadId == gameThread, "Caravan capture left the caller's game thread.");
            Captured++;
            if (CaptureThrowOnce) { CaptureThrowOnce = false; throw new InvalidOperationException("capture failure"); }
            return new(Items, CaptureRejected ? "not transferable" : null);
        }
        public ClientGiftRemoval Remove()
        { Removed++; if (RemovalException) throw new InvalidOperationException("partial remove"); return new(!RemovalFailure, "removed"); }
        public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context,
            ClientGiftCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
        {
            Context = context; Sent = command; Package = snapshot; Payload = payload; Submissions++; Submitted.TrySetResult();
            return Hold?.Task ?? Task.FromResult(TransportFailure ? ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Failed("HttpRequestException", "offline") : Success());
        }
        internal ClashOfRimClientNetworkResult<ModEventCreationResponseDto> Success() => ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Ok(
            new() { Result = new() { Accepted = !ServerRejection, ErrorCode = 1 }, EventId = "gift", AppliedSnapshotId = "accepted", NextLineageToken = "lineage" });
        public string Text(string key, params (string Name, object? Value)[] arguments) => key;
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public void Status(string message) { }
        public void Message(string message, bool rejected, bool forced = false) { }
        public void Diagnose(Exception exception) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
