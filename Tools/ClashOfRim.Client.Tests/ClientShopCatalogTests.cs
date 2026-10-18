using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientShopCatalogTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await CacheAndCommands();
                await FailuresAndAdmission();
            }
            foreach (string change in new[] { "user", "colony", "auth", "server", "game", "login", "reset", "generation" })
            {
                foreach (string kind in new[] { "list", "upsert", "remove" }) await LateCompletion(change, kind);
                await BoundActions(change);
            }
            await LatePreviews();
            await HydrationFailureAndSupersession();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task CacheAndCommands()
    {
        using var f = new Fixture();
        var listing = Listing("a");
        await f.Load(listing);
        int version = f.State.Version;
        listing.Item!.Metadata["label"] = "external";
        var copy = f.State.CopyListings();
        copy[0].Item!.ThingPackage!.Scribe!.XmlGzipBase64 = "external";
        copy[0].Item!.PawnPackage!.PendingPreviewPng![0] = 9;
        copy.Clear();
        Require(f.State.CopyListings()[0].Item!.Metadata["label"] == "中文 à"
            && f.State.CopyListings()[0].Item!.ThingPackage!.Scribe!.XmlGzipBase64 == "payload"
            && f.State.CopyListings()[0].Item!.PawnPackage!.PendingPreviewPng![0] == 1, "Shop exposed mutable cache/package DTOs.");
        (await f.Ports.Next())();
        Require(f.Previews == 1 && f.State.CopyListings()[0].Item!.Metadata["label"] == "中文 à", "Preview adapter mutated catalogue state.");

        var edit = Edit("a");
        Task update = f.Catalog.UpsertAsync(edit);
        edit.Item.Metadata["label"] = "edited after send";
        edit.Item.PawnPackage!.PendingPreviewPng![0] = 8;
        var call = await f.Next();
        Require(call.Edit!.Item.Metadata["label"] == "中文 à" && call.Edit.Item.PawnPackage!.PendingPreviewPng![0] == 1,
            "Shop command shares its editor's package data.");
        Require(call.Context.UserId == "alice" && call.Context.ColonyId == "colony" && call.Context.AuthToken == "auth", "Shop did not capture identity.");
        string status = f.State.Message;
        await f.Catalog.RefreshAsync(); await f.Catalog.RemoveAsync("a");
        Require(f.Calls == 2 && f.State.Busy && f.State.Message == status, "Concurrent shop requests disturbed the owner.");
        f.Ports.Snapshot = "new-base"; // Catalogue responses are not save commits.
        var replacement = Listing("a"); replacement.StockCount = 17;
        call.Complete(new ModUpsertServerShopListingResponseDto { Result = Accepted(), Listing = replacement }); await update;
        Require(f.State.CopyListings().Single().StockCount == 17 && f.State.Version > version && !f.State.Busy, "Upsert did not replace the listing after a normal snapshot advance.");
        replacement.StockCount = 999;
        Require(f.State.CopyListings()[0].StockCount == 17, "Upsert kept the transport's DTO reference.");
        (await f.Ports.Next())();

        Task insert = f.Catalog.UpsertAsync(Edit("b")); call = await f.Next();
        call.Complete(new ModUpsertServerShopListingResponseDto { Listing = Listing("b") }); await insert;
        Require(f.State.CopyListings().Count == 2, "Upsert lost existing catalogue entries.");
        Task remove = f.Catalog.RemoveAsync("a"); call = await f.Next();
        Require(call.Id == "a", "Remove lost its listing ID.");
        call.Complete(new ModRemoveServerShopListingResponseDto { Result = Accepted() }); await remove;
        Require(f.State.CopyListings().Single().ListingId == "b" && f.State.Message == "StatusListingRemoved", "Remove did not update the cache.");
        version = f.State.Version;
        remove = f.Catalog.RemoveAsync("absent"); call = await f.Next(); call.Complete(new ModRemoveServerShopListingResponseDto()); await remove;
        Require(f.State.Version == version, "Removing an absent row dirtied the view.");
        insert = f.Catalog.UpsertAsync(Edit("absent")); call = await f.Next(); call.Complete(new ModUpsertServerShopListingResponseDto()); await insert;
        Require(f.State.Version == version && f.State.CopyListings().Count == 1, "Optional absent upsert row cleared the catalogue.");
        await f.Load();
        Require(f.State.CopyListings().Count == 0 && !f.State.Busy && f.State.Message.Contains("COUNT=0"), "Empty refresh kept stale rows.");
        Require(f.Ports.Captures == 0 && f.Ports.Published == 0 && !f.Mutations.IsPending, "Catalogue operations changed save/mutation state.");
    }

    private static async Task FailuresAndAdmission()
    {
        using var f = new Fixture();
        await f.Load(Listing("keep"));
        foreach (string kind in new[] { "list", "upsert", "remove" })
        foreach (string failure in new[] { "transport", "rejected", "null", "exception", "cancelled", "hydrate" })
        {
            if (kind == "remove" && failure == "hydrate") continue;
            f.HydrationFailure = failure == "hydrate" ? new InvalidOperationException("hydrate") : null;
            Task work = f.Start(kind); var call = await f.Next();
            if (failure is "exception" or "cancelled") call.Completion.SetException(failure == "exception" ? new InvalidOperationException("failure") : new OperationCanceledException());
            else if (failure == "transport") call.Completion.SetResult(new Failure("HttpError", "network"));
            else if (failure == "null") call.Completion.SetResult(new Empty());
            else call.Complete(Response(kind, failure != "rejected", "new"));
            await work;
            Require(!f.State.Busy && f.State.CopyListings().Single().ListingId == "keep", "Failed " + kind + "/" + failure + " changed cache or retained busy state.");
            Require(f.State.Message.Contains(failure is "exception" or "cancelled" or "hydrate" ? "StatusException" : failure == "rejected" ? "Rejected" : "Failed"), "Shop failure lost semantic status.");
        }
        f.HydrationFailure = null;
        int calls = f.Calls;
        f.Administrator = false;
        await f.Catalog.UpsertAsync(Edit("x")); await f.Catalog.RemoveAsync("x");
        Require(f.Calls == calls && f.State.Message == "not-admin", "Non-admin changed the catalogue.");
        f.Administrator = true;
        var boundEdit = f.Catalog.BindUpsert(); f.Administrator = false; boundEdit(Edit("x"));
        Require(f.Calls == calls, "Open editor retained revoked administrator authority."); f.Administrator = true;
        foreach (string blocked in new[] { "pending", "base", "configured" })
        {
            if (blocked == "pending") f.Mutations.Begin(LocalMutationKind.Bank, "pending");
            if (blocked == "base") f.Ports.Snapshot = "";
            if (blocked == "configured") f.Ports.User = "";
            foreach (string kind in new[] { "list", "upsert", "remove" }) await f.Start(kind);
            Require(f.Calls == calls && !f.State.Busy, "Shop " + blocked + " admission was bypassed or stranded a request.");
            f.Mutations.Reset(); f.Ports.Snapshot = "base"; f.Ports.User = "alice";
        }
        await f.Catalog.RemoveAsync(""); Require(f.Calls == calls && f.State.Message == "StatusMissingListing", "Missing listing ID was sent.");
    }

    private static async Task LateCompletion(string change, string kind)
    {
        using var f = new Fixture();
        Task old = f.Start(kind); var oldCall = await f.Next();
        f.Change(change);
        Task current = f.Catalog.RefreshAsync(); var currentCall = await f.Next();
        oldCall.Complete(Response(kind, true, "old")); await old;
        Require(f.State.Busy && f.State.CopyListings().Count == 0 && f.State.Message == "StatusRefreshing" && f.Hydrations == 0,
            "Old " + kind + "/" + change + " response/finalizer interfered with the new request.");
        currentCall.Complete(Response("list", true, "new")); await current;
        Require(!f.State.Busy && f.State.CopyListings().Single().ListingId == "new", "Current shop request failed after old cleanup.");
    }

    private static async Task BoundActions(string change)
    {
        using var f = new Fixture();
        int invoked = 0;
        Action action = f.Catalog.Bind(() => invoked++);
        var edit = f.Catalog.BindUpsert();
        var remove = f.Catalog.BindRemove("x");
        f.Change(change); action(); edit(Edit("x")); remove();
        Require(invoked == 0 && f.Calls == 0, "Old confirmation/editor borrowed " + change + " identity.");
        // A new action on the same colony may use its current save revision.
        action = f.Catalog.Bind(() => invoked++); f.Ports.Snapshot = "advanced"; action();
        Require(invoked == 1, "Normal snapshot advance invalidated a catalogue action.");
        await Task.CompletedTask;
    }

    private static async Task LatePreviews()
    {
        using var f = new Fixture();
        await f.Load(Listing("old")); var oldPreview = await f.Ports.Next();
        await f.Load(Listing("new")); var newPreview = await f.Ports.Next();
        oldPreview(); Require(f.Previews == 0, "Old version installed stale pawn previews.");
        newPreview(); Require(f.Previews == 1, "Current version did not prepare previews.");
        await f.Load(Listing("logout")); oldPreview = await f.Ports.Next();
        f.Change("login"); oldPreview(); Require(f.Previews == 1 && f.State.CopyListings().Count == 0, "Old identity installed pawn previews.");
    }

    private static async Task HydrationFailureAndSupersession()
    {
        using var f = new Fixture();
        f.HydrationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task old = f.Catalog.RefreshAsync(); var call = await f.Next(); call.Complete(Response("list", true, "old"));
        await f.HydrationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Change("auth");
        Task current = f.Catalog.RefreshAsync(); var newCall = await f.Next();
        f.HydrationGate.SetResult(); await old;
        Require(f.State.Busy && f.State.CopyListings().Count == 0, "Hydration completion changed the replacement request.");
        f.HydrationGate = null; newCall.Complete(Response("list", true, "new")); await current;
    }

    private static ModProtocolResponseDto Accepted() => new() { Accepted = true };
    private static ModServerShopListingDto Listing(string id) => new() { ListingId = id, StockCount = 3, PriceSilver = 10, Item = new()
    { DefName = "Steel", StackCount = 2, Metadata = new() { ["label"] = "中文 à" }, PawnPackage = new() { PendingPreviewPng = new byte[] { 1, 2 } },
        ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "payload" } } } };
    private static ClientShopListingEdit Edit(string id) => new(id, "BuyFromPlayer", Listing(id).Item!, 7, 3, 1.25, "AtMost", "AtLeast");
    private static object Response(string kind, bool accepted, string id)
    {
        var result = new ModProtocolResponseDto { Accepted = accepted, ErrorCode = 1, Message = "server" };
        return kind switch
        {
            "list" => new ModListServerShopResponseDto { Result = result, Listings = new() { Listing(id) } },
            "upsert" => new ModUpsertServerShopListingResponseDto { Result = result, Listing = Listing(id) },
            "remove" => new ModRemoveServerShopListingResponseDto { Result = result },
            _ => throw new InvalidOperationException(kind)
        };
    }
    private sealed record Failure(string Code, string Message);
    private sealed record Empty;
    internal sealed record Call(string Kind, ClashOfRimClientNetworkContext Context, CancellationToken Token, ClientShopListingEdit? Edit = null, string? Id = null)
    {
        internal readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(object response) => Completion.SetResult(response);
    }
    internal sealed class Fixture : IDisposable, IClientShopCatalogTransport, IClientShopPresentation
    {
        internal readonly EventModuleTestPorts Ports;
        internal readonly ClientLocalMutationCoordinator Mutations;
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientShopState State;
        internal readonly ClientShopCatalog Catalog;
        private readonly ConcurrentQueue<Call> calls = new();
        private readonly SemaphoreSlim ready = new(0);
        internal bool Administrator = true;
        internal int Calls, Previews, Hydrations;
        internal Exception? HydrationFailure;
        internal TaskCompletionSource? HydrationGate;
        internal readonly TaskCompletionSource HydrationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture(EventModuleTestPorts? ports = null, ClientSessionLifecycle? session = null,
            ClientShopState? state = null, ClientLocalMutationCoordinator? mutations = null)
        {
            if (session is null) ClientSessionWork.Reset();
            Ports = ports ?? new(); Mutations = mutations ?? new();
            Session = session ?? new(Ports, Ports, new ClientSessionConnection(Ports)); State = state ?? new(Session, Ports, this);
            Catalog = new(State, Mutations, this, this, () => Administrator);
        }
        internal async Task Load(params ModServerShopListingDto[] listings)
        {
            Task work = Catalog.RefreshAsync(); var call = await Next();
            call.Complete(new ModListServerShopResponseDto { Result = Accepted(), Listings = listings.ToList() }); await work;
        }
        internal Task Start(string kind) => kind switch
        { "list" => Catalog.RefreshAsync(), "upsert" => Catalog.UpsertAsync(Edit("a")), "remove" => Catalog.RemoveAsync("a"), _ => throw new InvalidOperationException(kind) };
        internal async Task<Call> Next()
        {
            if (!await ready.WaitAsync(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Shop did not dispatch an expected request.");
            Require(calls.TryDequeue(out var call), "Shop call queue lost work."); return call!;
        }
        private async Task<ClashOfRimClientNetworkResult<T>> Send<T>(Call call)
        {
            Calls++; calls.Enqueue(call); ready.Release();
            var result = await call.Completion.Task;
            return result switch { Failure failure => ClashOfRimClientNetworkResult<T>.Failed(failure.Code, failure.Message),
                Empty => ClashOfRimClientNetworkResult<T>.Ok(default!), _ => ClashOfRimClientNetworkResult<T>.Ok((T)result) };
        }
        public Task<ClashOfRimClientNetworkResult<ModListServerShopResponseDto>> ListAsync(ClashOfRimClientNetworkContext c, CancellationToken t) => Send<ModListServerShopResponseDto>(new("list", c, t));
        public Task<ClashOfRimClientNetworkResult<ModUpsertServerShopListingResponseDto>> UpsertAsync(ClashOfRimClientNetworkContext c, ClientShopListingEdit e, CancellationToken t) => Send<ModUpsertServerShopListingResponseDto>(new("upsert", c, t, e));
        public Task<ClashOfRimClientNetworkResult<ModRemoveServerShopListingResponseDto>> RemoveAsync(ClashOfRimClientNetworkContext c, string id, CancellationToken t) => Send<ModRemoveServerShopListingResponseDto>(new("remove", c, t, Id: id));
        public async Task HydrateAsync(ClashOfRimClientNetworkContext c, IReadOnlyList<ModServerShopListingDto> listings, CancellationToken token)
        {
            Hydrations++;
            if (HydrationFailure is not null) throw HydrationFailure;
            if (HydrationGate is { } gate) { HydrationStarted.TrySetResult(); await gate.Task; }
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break;
                case "auth": Ports.Auth = "new"; break; case "server": Ports.Server = "http://new"; break;
                case "base": Ports.Snapshot = "new"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth }); break;
                case "reset": State.Reset(); break; case "generation": ClientSessionWork.Reset(); break;
            }
        }
        public string NotAdministrator => "not-admin";
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public void PreparePreviews(IReadOnlyList<ModServerShopListingDto> listings) { Previews++; listings[0].Item!.Metadata["label"] = "preview side effect"; }
        public void Diagnose(Exception exception) { }
        public void Dispose() { ClientSessionWork.Reset(); ready.Dispose(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
