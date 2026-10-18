using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Protocol;

internal static class MultiplayerShopPanelTests
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
                await ProjectionAndModes(); await RefreshAndAvailability();
                foreach (bool sale in new[] { false, true }) await Purchase(sale);
            }
            foreach (string change in new[] { "close", "mode", "user", "colony", "auth", "server", "game", "login", "reset", "generation" })
            foreach (bool sale in new[] { false, true }) await ObsoleteConfirmation(sale, change);
            await LateCatalog(); await LargeCatalog(); await BusyConfirmation();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static async Task ProjectionAndModes()
    {
        using var f = new Fixture();
        Check(f.Panel.Mode == ServerShopListingKinds.SellToPlayer && f.Panel.Read().All.Count == 0, "Initial shop view leaked data.");
        var first = Listing("I", "SellToPlayer"); var buy = Listing("i", "BuyFromPlayer");
        await f.Catalog.Load(first, buy, Listing("İ", "buyfromplayer"), Listing("legacy", "unknown"));
        var view = f.Panel.Read();
        Check(view.All.Select(l => l.ListingId).SequenceEqual(new[] { "I", "i", "İ", "legacy" })
            && view.SellToPlayer.Select(l => l.ListingId).SequenceEqual(new[] { "I", "İ", "legacy" })
            && view.BuyFromPlayer.Single().ListingId == "i", "Filtering changed catalog order or normalized kinds using culture/case.");
        Check(ReferenceEquals(view.All[0], view.SellToPlayer[0]) && ReferenceEquals(view.All[1], view.BuyFromPlayer[0]), "Administrator and player projection cloned separate rows.");
        first.Item!.Metadata["label"] = "outside";
        view.SellToPlayer[0].Item!.Metadata["label"] = "view";
        view.SellToPlayer[0].Item!.PawnPackage!.PendingPreviewPng![0] = 9;
        var stored = f.Operations.State.CopyListings()[0];
        Check(stored.Item!.Metadata["label"] == "中文 à" && stored.Item.PawnPackage!.PendingPreviewPng![0] == 1, "Display edits changed business item or non-wire preview bytes.");
        Check(f.Panel.SelectMode("BuyFromPlayer") && !f.Panel.SelectMode("BuyFromPlayer"), "Mode selection did not distinguish a real change.");
        Check(ReferenceEquals(view, f.Panel.Read()) && f.Catalog.Calls == 1, "Mode switch rebuilt or refetched the catalog.");
        Check(f.Panel.SelectMode("buyfromplayer") && f.Panel.Mode == "SellToPlayer", "Unknown mode did not use shared normalization.");
        await f.Catalog.Load(Listing("replacement", "BuyFromPlayer"));
        Check(!ReferenceEquals(view, f.Panel.Read()) && f.Panel.Read().All.Count == 1 && f.Panel.Read().SellToPlayer.Count == 0, "Updated catalog retained old projection or counts.");
        f.Panel.SelectMode("BuyFromPlayer"); var current = f.Panel.Read(); var owner = f.Panel.Ownership;
        f.Operations.Change("snapshot");
        Check(ReferenceEquals(current, f.Panel.Read()) && ReferenceEquals(owner, f.Panel.Ownership) && f.Panel.Mode == "BuyFromPlayer", "Ordinary save advance discarded view state.");
        f.Panel.Close();
        Check(f.Panel.Mode == "SellToPlayer" && !ReferenceEquals(owner, f.Panel.Ownership) && f.Operations.State.CopyListings().Count == 1,
            "Closing retained page ownership or cleared shared catalog.");
        f.Panel.SelectMode("BuyFromPlayer"); f.Operations.Change("auth");
        Check(f.Panel.Mode == "SellToPlayer" && f.Panel.Read().All.Count == 0, "Replacement login retained filter or rows.");
    }

    private static async Task RefreshAndAvailability()
    {
        using var f = new Fixture();
        var listing = Listing("listing", "SellToPlayer");
        Check(f.Operations.Sync.TryBeginManual(out var busy), "Cannot occupy manual synchronization.");
        await f.Panel.RefreshAsync(10); Check(f.Catalog.Calls == 0 && !f.Panel.Quote(listing).CanPurchase, "Automatic refresh or purchase crossed manual synchronization.");
        Task read = f.Panel.RefreshAsync(10, manual: true); var sent = await f.Catalog.Next();
        Check(!f.Panel.CanRefresh && !f.Panel.Quote(listing).CanPurchase, "Catalog request did not disable refresh/purchase.");
        await f.Panel.RefreshAsync(40, manual: true); Check(f.Catalog.Calls == 1, "Explicit refresh duplicated an active request.");
        sent.Complete(Response(listing)); await read;
        Check(busy!.IsCurrent && f.Panel.CanRefresh, "Read disposed another operation's lease or stayed busy."); busy.Dispose();
        await f.Panel.RefreshAsync(39.9f); Check(f.Catalog.Calls == 1, "Skipped busy refresh consumed the deadline or refreshed too early.");
        await Reply(f, f.Panel.RefreshAsync(40), listing);
        f.Panel.RequestRefresh(); await Reply(f, f.Panel.RefreshAsync(40), listing);
        await Reply(f, f.Panel.RefreshAsync(40, manual: true), listing);
        Check(f.Catalog.Calls == 4 && f.Panel.Status == f.Operations.State.Message, "Periodic/manual/tab refresh or status projection changed.");

        listing.BasePriceSilver = 10; listing.PriceSilver = 999; listing.PriceIncreaseRatio = 1.5; listing.BuyerPurchaseCount = 2;
        var quote = f.Panel.Quote(listing);
        Check(quote.Count == 1 && quote.TotalPrice == ServerShopPricing.TotalPrice(10, 1.5, 2, 1) && quote.Available && quote.CanPurchase && !quote.BuyFromPlayer,
            "Tile quoted a different batch or duplicated dynamic pricing.");
        listing.BasePriceSilver = 0; listing.PriceSilver = 7; listing.BuyerPurchaseCount = -3;
        Check(f.Panel.Quote(listing).TotalPrice == 7, "Legacy price or negative lifetime-count fallback changed.");
        listing.ListingKind = "BuyFromPlayer"; listing.StockCount = 0;
        Check(f.Panel.Quote(listing) is { BuyFromPlayer: true, Available: false, CanPurchase: false, Count: 1 }, "Exhausted demand remained tradable.");
        listing.StockCount = -1; Check(!f.Panel.Quote(listing).Available, "Negative stock became unlimited.");
        listing.StockCount = 1; listing.Item = null;
        Check(f.Panel.Quote(listing) is { Available: true, CanPurchase: false }, "Missing item enabled purchase.");

        read = f.Panel.RefreshAsync(41, manual: true); sent = await f.Catalog.Next();
        sent.Complete(new ModListServerShopResponseDto { Result = new() { Accepted = false, Message = "rejected" } }); await read;
        Check(f.Panel.Read().All.Count == 1 && f.Panel.CanRefresh && f.Panel.Status.Contains("Rejected", StringComparison.Ordinal), "Failed refresh discarded the previous valid catalog or stayed busy.");
        f.Panel.Close(); await Reply(f, f.Panel.RefreshAsync(42));
        Check(f.Panel.Read().All.Count == 0 && f.Panel.Mode == "SellToPlayer", "Reopening did not request immediately or accept an empty catalog.");
    }

    private static async Task Purchase(bool sale)
    {
        using var f = new Fixture(sale);
        var listing = f.Operations.Listing;
        listing.BasePriceSilver = 10; listing.PriceIncreaseRatio = 1.5; listing.BuyerPurchaseCount = 2;
        listing.Item!.PawnPackage = new() { PendingPreviewPng = new byte[] { 1, 2 } };
        if (sale) f.Panel.SelectMode("BuyFromPlayer");
        var quote = f.Panel.Quote(listing); Action confirm = f.Panel.PreparePurchase(listing);
        Check(!f.Panel.SelectMode(f.Panel.Mode), "Same mode unexpectedly invalidated intent.");
        listing.StockCount = 0; listing.PriceSilver = 999; listing.BasePriceSilver = 999; listing.Item.StackCount = 99;
        listing.Item.Metadata["data"] = "mutated"; listing.Item.PawnPackage.PendingPreviewPng[0] = 9;
        listing.ListingKind = sale ? "SellToPlayer" : "BuyFromPlayer";
        f.Operations.Change("snapshot"); confirm();
        await f.Operations.Ports.PumpUntil(f.Operations.Completion.Task);
        var command = f.Operations.Command!;
        Check(command.PurchaseCount == 1 && command.TotalPrice == quote.TotalPrice && command.ItemCount == 3
            && command.Listing.Item!.Metadata["data"] == "original" && command.Listing.Item.PawnPackage!.PendingPreviewPng![0] == 1,
            "Confirmation lost its frozen batch, quote, metadata or preview bytes.");
        Check(command.Listing.ListingKind == (sale ? "BuyFromPlayer" : "SellToPlayer") && f.Operations.Context!.CurrentSnapshotId == "new"
            && f.Operations.Applied == 1 && f.Operations.Submissions == 1 && f.Operations.Completed == 1,
            "Confirmation changed buy/sell direction, used an old snapshot or duplicated settlement.");
    }

    private static async Task ObsoleteConfirmation(bool sale, string change)
    {
        using var f = new Fixture(sale);
        if (sale) f.Panel.SelectMode("BuyFromPlayer");
        Action confirm = f.Panel.PreparePurchase(f.Operations.Listing);
        if (change == "close") f.Panel.Close();
        else if (change == "mode")
        {
            string original = f.Panel.Mode;
            f.Panel.SelectMode(sale ? "SellToPlayer" : "BuyFromPlayer"); f.Panel.SelectMode(original);
        }
        else f.Operations.Change(change);
        confirm();
        await f.Operations.Ports.PumpUntil(Task.CompletedTask);
        Check(f.Operations.Validations == 0 && f.Operations.Applied == 0 && f.Operations.Submissions == 0
            && !f.Operations.Mutations.IsPending && !f.Operations.Sync.IsBusy, $"Old {(sale ? "sell" : "buy")} confirmation crossed {change}.");
    }

    private static async Task BusyConfirmation()
    {
        using var f = new Fixture();
        Action confirm = f.Panel.PreparePurchase(f.Operations.Listing);
        Task read = f.Panel.RefreshAsync(1); var sent = await f.Catalog.Next();
        confirm(); Check(f.Operations.Applied == 0 && f.Operations.State.Busy, "Confirmation disturbed a catalog request already in progress.");
        sent.Complete(Response()); await read;
        Check(f.Operations.Sync.TryBeginManual(out var lease), "Cannot occupy synchronization.");
        confirm(); Check(f.Operations.Applied == 0 && lease!.IsCurrent, "Delayed confirmation bypassed current business admission."); lease!.Dispose();
        confirm(); await f.Operations.Ports.PumpUntil(f.Operations.Completion.Task);
        Check(f.Operations.Applied == 1 && f.Operations.Submissions == 1, "Valid confirmation stopped after temporary business contention.");
    }

    private static async Task LateCatalog()
    {
        using var f = new Fixture();
        Task old = f.Panel.RefreshAsync(1); var stale = await f.Catalog.Next();
        f.Panel.SelectMode("BuyFromPlayer"); f.Operations.Change("auth");
        Task current = f.Panel.RefreshAsync(2); var fresh = await f.Catalog.Next();
        stale.Complete(Response(Listing("old", "BuyFromPlayer"))); await old;
        Check(f.Panel.Mode == "SellToPlayer" && f.Panel.Read().All.Count == 0 && !f.Panel.CanRefresh, "Old response changed a replacement session's page.");
        fresh.Complete(Response(Listing("new", "SellToPlayer"))); await current;
        Check(f.Panel.Read().All.Single().ListingId == "new", "Current catalog failed after obsolete cleanup.");
        old = f.Panel.RefreshAsync(3, manual: true); stale = await f.Catalog.Next(); f.Panel.Close();
        stale.Complete(Response(Listing("shared", "SellToPlayer"))); await old;
        Check(f.Panel.Read().All.Single().ListingId == "shared", "Closing one page cancelled a valid shared catalog update.");
    }

    private static async Task LargeCatalog()
    {
        using var f = new Fixture();
        await f.Catalog.Load(Enumerable.Range(0, 4000).Select(i => Listing(i.ToString(CultureInfo.InvariantCulture), i % 2 == 0 ? "BuyFromPlayer" : "SellToPlayer")).ToArray());
        var view = f.Panel.Read();
        Check(view.All.Count == 4000 && view.SellToPlayer.Count == 2000 && view.BuyFromPlayer.Count == 2000, "Large catalog lost or duplicated rows.");
        for (int i = 0; i < 20; i++)
        {
            f.Panel.SelectMode(i % 2 == 0 ? "BuyFromPlayer" : "SellToPlayer");
            Check(ReferenceEquals(view, f.Panel.Read()), "Mode switch copied the large catalog again.");
        }
        Check(f.Catalog.Calls == 1, "Local mode switches issued network reads.");
    }

    private static ModServerShopListingDto Listing(string id, string kind) => new()
    {
        ListingId = id, ListingKind = kind, StockCount = 3, PriceSilver = 10, PriceIncreaseRatio = 1,
        Item = new() { DefName = "Steel", StackCount = 3, Metadata = new() { ["label"] = "中文 à" }, PawnPackage = new() { PendingPreviewPng = new byte[] { 1, 2 } } }
    };
    private static ModListServerShopResponseDto Response(params ModServerShopListingDto[] listings) =>
        new() { Result = new() { Accepted = true }, Listings = listings.ToList() };
    private static async Task Reply(Fixture f, Task read, params ModServerShopListingDto[] listings)
    { (await f.Catalog.Next()).Complete(Response(listings)); await read; }
    private sealed class Fixture : IDisposable
    {
        internal readonly ClientShopOperationsTests.Fixture Operations;
        internal readonly ClientShopCatalogTests.Fixture Catalog;
        internal readonly ClientShopPanel Panel;
        internal Fixture(bool sale = false)
        {
            Operations = new(sale);
            Catalog = new(Operations.Ports, Operations.Session, Operations.State, Operations.Mutations);
            Panel = new(Operations.Session, Operations.Ports, Operations.State, Catalog.Catalog, Operations.Operations, Operations.Sync);
        }
        public void Dispose() { Catalog.Dispose(); Operations.Dispose(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
