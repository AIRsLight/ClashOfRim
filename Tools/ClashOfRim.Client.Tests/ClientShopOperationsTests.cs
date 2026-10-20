using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientShopOperationsTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool sale in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                await Success(sale);
                await Retry(sale, build: false); await Retry(sale, build: true);
            }
            foreach (bool sale in new[] { false, true })
            {
                foreach (Fault fault in Enum.GetValues<Fault>()) if (fault != Fault.None) await Failure(sale, fault);
                foreach (string change in new[] { "user", "colony", "auth", "server", "snapshot", "game", "login", "generation", "reset" })
                { await LateCompletion(sale, change, queued: false); await LateCompletion(sale, change, queued: true); }
                await RetryOwnership(sale);
                await Admission(sale);
            }
            await Hydration();
            await QuantityBounds();
            await Confirmation();
            await CompletionStartsRefresh();
            await CompletionReleaseReplacement();
            await PartialPublication();
        }
        finally { CultureInfo.CurrentCulture = original; ClientSessionWork.Reset(); }
    }

    private static async Task Success(bool sale)
    {
        using var f = new Fixture(sale);
        f.Count = 2;
        Task work = f.Start();
        await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Applied == 1 && f.Validations == 1 && f.Ports.Captures == 1 && f.Sync.SnapshotInProgress && f.State.Busy && f.Mutations.IsPending, "Shop repeated immediate validation or prepared outside its admission.");
        f.Listing.ListingId = "edited"; f.Listing.Item!.Metadata["data"] = "edited";
        f.Delivered[0].ThingPackage!.Scribe!.XmlGzipBase64 = "edited";
        (await f.Ports.Next())(); await work;
        Require(f.Command!.Listing.ListingId == "listing" && f.Command.Listing.Item!.Metadata["data"] == "original"
            && f.Command.ItemCount == 6 && f.Command.TotalPrice == 20 && f.Command.PurchaseCount == 2
            && f.Command.Delivered[0].ThingPackage!.Scribe!.XmlGzipBase64 == "payload", "Shop did not freeze its quote or package data.");
        Require(f.Context!.AuthToken == "auth" && f.Context.CurrentSnapshotId == "base" && f.Ports.Snapshot == "accepted"
            && !f.State.Busy && !f.Sync.IsBusy && !f.Mutations.IsPending && f.Completed == 1, "Shop failed owned publication/cleanup.");
        Require(f.State.Message.StartsWith(sale ? "StatusSoldToServer" : "StatusPurchased", StringComparison.Ordinal), "Shop reversed buy/sell success semantics.");
    }

    private static async Task Failure(bool sale, Fault fault)
    {
        using var f = new Fixture(sale) { Failure = fault };
        if (fault == Fault.Hydration) { if (sale) return; f.RequirePackages(); }
        Task work = f.Start();
        if (fault is Fault.MissingSnapshot or Fault.MissingListing or Fault.WrongListing) (await f.Ports.Next())();
        await work;
        bool changed = fault is not (Fault.Validation or Fault.BeforeApply or Fault.BeforeApplyThrow or Fault.Hydration);
        if (changed) (await f.Ports.Next())();
        Require(!f.Sync.IsBusy && !f.State.Busy && f.Ports.Published == 0 && f.Completed == 0, "Shop failure retained admission or published: " + fault);
        Require(f.Mutations.IsPending == changed && (f.View.Retry is not null) == (fault == Fault.Transport), "Shop recovery misclassified partial effects: " + fault);
        if (fault is Fault.PartialApply or Fault.PartialApplyThrow or Fault.BeforeApply or Fault.BeforeApplyThrow)
            Require(f.Submissions == 0 && f.Ports.Captures == 0, "Incomplete local exchange was packaged/uploaded.");
    }

    private static async Task Retry(bool sale, bool build)
    {
        using var f = new Fixture(sale) { Failure = build ? Fault.None : Fault.Transport };
        if (build) f.Ports.CaptureFailure = new("capture", "capture failed", SnapshotUploadMessage.SaveFailed);
        await f.Start(); (await f.Ports.Next())();
        Require(f.View.Retry is not null && f.Applied == 1 && !f.State.Busy && !f.Sync.IsBusy, "Prepared shop failure did not offer an upload retry.");
        var key = f.Command?.Key; var package = f.Package; var payload = f.Payload;
        int captures = f.Ports.Captures;
        f.Failure = Fault.None; f.Ports.CaptureFailure = null;
        f.Listing.PriceSilver = 999; f.Listing.StockCount = 0; f.Listing.Item!.Metadata["data"] = "changed";
        var retry = f.View.Retry!;
        retry(); retry(); // Failure decisions are consumed once by the shared coordinator.
        var accept = await f.Ports.Next(); accept();
        await f.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Applied == 1 && f.Command!.TotalPrice == 10 && f.Command.Listing.Item!.Metadata["data"] == "original"
            && f.Ports.Captures == captures + (build ? 1 : 0) && f.Submissions == (build ? 1 : 2), "Retry reapplied inventory, recaptured a prepared save or changed its quote.");
        if (!build) Require(f.Command!.Key == key && ReferenceEquals(f.Package, package) && ReferenceEquals(f.Payload, payload), "Retry did not reuse the original snapshot/key/bytes.");
        Require(!f.Mutations.IsPending && !f.Sync.IsBusy && !f.State.Busy, "Retry did not complete cleanup.");
    }

    private static async Task LateCompletion(bool sale, string change, bool queued)
    {
        using var f = new Fixture(sale);
        if (!queued) f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = f.Start(); await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Action? publish = queued ? await f.Ports.Next() : null;
        f.Change(change); f.Sync.Reset(); f.State.Reset(); f.Mutations.Reset();
        Require(f.Sync.TryBeginManual(out var replacement), "Cannot create replacement lease.");
        var request = f.State.Begin(replacement)!;
        if (queued) publish!(); else f.Hold!.SetResult(f.Success());
        await work;
        Require(f.Ports.Published == 0 && f.Completed == 0 && replacement!.IsCurrent && f.State.Busy, "Old " + change + " completion released/published into its replacement.");
        f.State.End(request);
    }

    private static async Task RetryOwnership(bool sale)
    {
        using var f = new Fixture(sale) { Failure = Fault.Transport };
        await f.Start(); (await f.Ports.Next())();
        f.Ports.Snapshot = "new-base";
        f.View.Retry!(); (await f.Ports.Next())();
        Require(f.Submissions == 1 && f.View.Retry is null && f.View.Message == "base-changed" && f.Mutations.IsPending, "Shop retried against a new base snapshot.");
        using var busy = new Fixture(sale) { Failure = Fault.Transport };
        await busy.Start(); (await busy.Ports.Next())();
        busy.Sync.TryBeginManual(out var other);
        busy.View.Retry!(); (await busy.Ports.Next())();
        Require(busy.View.Retry is not null && other!.IsCurrent && busy.Submissions == 1, "Busy retry lost recovery or released another operation.");
        other!.Dispose();
        Action oldRetry = busy.View.Retry!;
        busy.Change("login"); oldRetry();
        Require(busy.Submissions == 1, "Old failure window borrowed a new login.");
    }

    private static async Task Admission(bool sale)
    {
        foreach (string reason in new[] { "unconfigured", "snapshot", "pending", "busy", "shop-busy", "listing", "item", "stock", "price", "quantity" })
        {
            using var f = new Fixture(sale);
            switch (reason)
            {
                case "unconfigured": f.Ports.User = ""; break; case "snapshot": f.Ports.Snapshot = ""; break;
                case "pending": f.Mutations.Begin(LocalMutationKind.Bank, "bank"); break;
                case "busy": f.Sync.TryBeginManual(out _); break; case "shop-busy": f.State.Begin(); break;
                case "listing": f.Listing.ListingId = ""; break; case "item": f.Listing.Item = null; break;
                case "stock": f.Listing.StockCount = 0; break; case "price": f.Listing.PriceSilver = int.MaxValue; break;
                case "quantity": f.Listing.Item!.StackCount = int.MaxValue; f.Count = 2; break;
            }
            await f.Start();
            Require(f.Applied == 0 && f.Submissions == 0 && f.Ports.Captures == 0, "Shop admission changed local state: " + reason);
            if (reason == "pending") Require(f.Mutations.Current?.Kind == LocalMutationKind.Bank, "Shop cleared a foreign pending mutation.");
            if (reason == "busy") Require(f.Sync.IsBusy, "Rejected shop released another domain lease.");
            if (reason is not ("busy" or "shop-busy")) Require(!f.State.Busy && !f.Sync.IsBusy, "Rejected shop stranded its own request.");
        }
    }

    private static async Task Hydration()
    {
        foreach (string change in new[] { "none", "user", "colony", "auth", "server", "snapshot", "game", "login", "generation", "reset", "validation" })
        {
            using var f = new Fixture(false); f.RequirePackages();
            Task work = f.Start(); var apply = await f.Ports.Next();
            Require(f.Applied == 0 && f.Hydrated == 1 && f.Sync.ManualInProgress && f.State.Busy && !f.Mutations.IsPending, "Shop mutated before package preparation or released admission too soon.");
            if (change == "validation") f.Failure = Fault.Validation; else f.Change(change);
            apply();
            if (change == "none") { (await f.Ports.Next())(); }
            await work;
            Require(f.Applied == (change == "none" ? 1 : 0) && !f.State.Busy && !f.Sync.IsBusy, "Shop hydration ignored ownership/revalidation: " + change);
        }
    }

    private static async Task Confirmation()
    {
        using var f = new Fixture(false);
        Action old = f.Operations.BindPurchase(f.Listing, 1);
        f.Change("login"); old();
        Require(f.Submissions == 0 && f.Applied == 0, "Old purchase dialog borrowed a new identity.");
        Action current = f.Operations.BindPurchase(f.Listing, 1);
        f.Listing.StockCount = 0; f.Listing.PriceSilver = 999; f.Ports.Snapshot = "next-base";
        current(); (await f.Ports.Next())(); await f.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Command!.TotalPrice == 10 && f.Context!.CurrentSnapshotId == "next-base", "Purchase confirmation lost its quote or disallowed a new command after normal save advance.");
    }

    private static async Task QuantityBounds()
    {
        foreach (bool sale in new[] { false, true })
        foreach (int count in new[] { -1, 0, 1 })
        {
            using var f = new Fixture(sale) { Count = count };
            f.Listing.Item!.StackCount = int.MaxValue;
            Task work = f.Start(); (await f.Ports.Next())(); await work;
            Require(f.Command!.ItemCount == int.MaxValue && f.Command.PurchaseCount == 1 && f.Completed == 1,
                "Shop quantity boundary overflowed or changed legacy count normalization.");
        }
    }

    private static async Task PartialPublication()
    {
        using var f = new Fixture(false);
        f.Ports.OnPublish = () => throw new InvalidOperationException("partial publication");
        Task work = f.Start();
        try { (await f.Ports.Next())(); } catch (InvalidOperationException) { }
        await work; (await f.Ports.Next())();
        Require(f.Ports.Published == 1 && f.Mutations.IsPending && f.View.Retry is null && f.Completed == 0 && !f.State.Busy,
            "Partial snapshot publication allowed a new purchase or replay.");
    }

    private static async Task CompletionStartsRefresh()
    {
        using var f = new Fixture(false);
        ClientGameRequestTracker.Request? refresh = null;
        f.Operations.Completed += step => refresh = f.State.Begin();
        Task work = f.Start(); (await f.Ports.Next())(); await work;
        Require(refresh is not null && f.State.Busy, "Purchase finalizer cleared the catalogue refresh started by completion.");
        f.State.End(refresh!);
    }

    private static async Task CompletionReleaseReplacement()
    {
        using var f = new Fixture(false);
        ClientGameRequestTracker.Request? replacement = null;
        f.Sync.SnapshotChanged += notice =>
        {
            if (notice.Phase == SnapshotTransactionPhase.Ended)
            {
                f.Session.PublishLogin(new() { AuthToken = "auth", SessionId = "replacement" });
                replacement = f.State.Begin();
            }
        };
        await f.Ports.PumpUntil(f.Start());
        Require(f.Completed == 0, "Shop completion refreshed a login installed during admission release.");
        Require(replacement is not null && f.State.Busy, "Shop release erased the request installed by the new login.");
        f.State.End(replacement!);
    }

    internal enum Fault { None, Validation, BeforeApply, BeforeApplyThrow, PartialApply, PartialApplyThrow, Hydration, Transport, Forbidden, Rejected, MissingSnapshot, MissingListing, WrongListing }
    internal sealed class Fixture : IDisposable, IClientShopRuntime, IClientShopSource, IClientShopPurchaseTransport, IClientShopPresentation, IClientShopOperationsPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientShopState State;
        internal readonly ClientShopOperations Operations;
        internal readonly EventModuleFailureView View;
        internal readonly ModServerShopListingDto Listing;
        internal readonly List<ModThingReferenceDto> Delivered = new() { new() { DefName = "Steel", StackCount = 3, ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "payload" } } } };
        internal Fault Failure;
        internal int Applied, Hydrated, Submissions, Completed, Validations, Count = 1;
        internal ClientShopCommand? Command;
        internal ClashOfRimClientNetworkContext? Context;
        internal ModSnapshotPackageMetadataDto? Package;
        internal byte[]? Payload;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto>>? Hold;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture(bool sale)
        {
            ClientSessionWork.Reset();
            Listing = new() { ListingId = "listing", ListingKind = sale ? "BuyFromPlayer" : "SellToPlayer", PriceSilver = 10, PriceIncreaseRatio = 1, StockCount = 3,
                Item = new() { DefName = "Steel", StackCount = 3, Metadata = new() { ["data"] = "original" } } };
            Session = new(Ports, Ports, new ClientSessionConnection(Ports)); State = new(Session, Ports, this); View = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports));
            Operations = new(State, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations, transactions,
                new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, View), this, this, this, this);
            Operations.Completed += step => { Require(!State.Busy && !Sync.IsBusy && !Mutations.IsPending, "Shop completed before cleanup."); Completed++; Completion.TrySetResult(); };
        }
        internal Task Start() => Operations.StartAsync(Listing, Count);
        internal void RequirePackages() { Listing.Item!.ThingPackageId = "complex-package"; }
        public IClientShopSource Capture() => this;
        public string? Validate(ClientShopCommand command) { Validations++; return Failure == Fault.Validation ? "invalid inventory" : null; }
        public ClientShopLocalResult Apply(ClashOfRimClientNetworkContext context, ClientShopCommand command, Action mutationStarting)
        {
            Require(Sync.ManualInProgress && Mutations.IsPending && State.Busy, "Local shop ran without its admission/mutation state.");
            Applied++;
            if (Failure is not (Fault.BeforeApply or Fault.BeforeApplyThrow)) mutationStarting();
            if (Failure is Fault.BeforeApplyThrow or Fault.PartialApplyThrow) throw new InvalidOperationException("local failure");
            return new(Failure is not (Fault.BeforeApply or Fault.PartialApply), Delivered, "local failure");
        }
        public Task HydrateAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModServerShopListingDto> listings, CancellationToken token)
        {
            Hydrated++;
            if (Failure == Fault.Hydration) throw new InvalidOperationException("missing package");
            Require(context.AuthToken == "auth" && !ReferenceEquals(listings[0], Listing), "Hydration borrowed identity or editor state.");
            listings[0].Item!.ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "downloaded" } };
            return Task.CompletedTask;
        }
        public Task<ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto>> PurchaseAsync(ClashOfRimClientNetworkContext context,
            ClientShopCommand command, ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token)
        {
            Context = context; Command = command; Package = package; Payload = payload; Submissions++; Sent.TrySetResult();
            return Hold?.Task ?? Task.FromResult(Failure is Fault.Transport or Fault.Forbidden
                ? ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto>.Failed("HttpError", Failure == Fault.Forbidden ? "403 forbidden" : "503 unavailable") : Success());
        }
        internal ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto> Success() => ClashOfRimClientNetworkResult<ModPurchaseServerShopListingResponseDto>.Ok(new()
        { Result = new() { Accepted = Failure != Fault.Rejected }, ListingId = Failure == Fault.MissingListing ? null : Failure == Fault.WrongListing ? "other" : "listing",
            AppliedSnapshotId = Failure == Fault.MissingSnapshot ? null : "accepted", NextLineageToken = "lineage", RemainingStockCount = 1 });
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break;
                case "auth": Ports.Auth = "new"; break; case "server": Ports.Server = "http://new"; break;
                case "snapshot": Ports.Snapshot = "new"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth }); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break; case "reset": State.Reset(); break;
            }
        }
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base-changed";
        public string NotAdministrator => "not-admin";
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string SnapshotBuildFailed(string? reason) => "build:" + reason;
        public void PreparePreviews(IReadOnlyList<ModServerShopListingDto> listings) { }
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
