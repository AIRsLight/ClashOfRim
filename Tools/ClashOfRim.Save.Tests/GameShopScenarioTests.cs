using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.GameTests;
using Microsoft.Data.Sqlite;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameShopScenarioTests
{
    internal static void PurchaseAndBuyback() => PurchaseAndBuybackAsync().GetAwaiter().GetResult();
    internal static void Catalog() => CatalogAsync().GetAwaiter().GetResult();
    internal static void Stress() => StressAsync().GetAwaiter().GetResult();
    private static async Task PurchaseAndBuybackAsync()
    {
        foreach (bool preserve in new[] { false, true })
        {
            using var f = await Fixture.Create();
            await f.Shop.Purchase(preserve);
            Check(f.Stocks["Silver"] == 990 && f.Stocks["Steel"] == 105 && f.Store.List().Count == (preserve ? 1 : 0), "Purchase changed balances or listing preservation.");
            Check(f.Attempts.SequenceEqual(new[] { 1 }) && f.Runtime.Calls.Single(c => c.Method == "time.step").Args.Int("ticks") == 600, "Purchase skipped original delivery ticks or repeated the order.");
        }
        foreach (string fault in new[] { "cash", "items", "short-cash" })
        {
            using var f = await Fixture.Create(fault);
            if (fault == "short-cash") f.Stocks["Silver"] = 9;
            await Reject(f.Shop.Purchase, fault);
        }
        foreach (string fault in new[] { "none", "items", "excess-cash", "empty-cash", "invalid-snapshot" })
        {
            using var f = await Fixture.Create(fault);
            if (fault != "none") { await Reject(f.Shop.Buyback, fault); continue; }
            await f.Shop.Buyback();
            Check(f.Attempts.SequenceEqual(new[] { 2, 1, 1 }) && f.Stocks["Silver"] == 1010 && f.Stocks["Steel"] == 95 && f.Store.List().Count == 0,
                "Buyback omitted excess/exhausted rejection or failed to remove its listing.");
        }
    }
    private static async Task CatalogAsync()
    {
        using (var full = await Fixture.Create())
        {
            await full.Catalog.Run();
            var report = full.Report();
            Check(report.Int("total") == 205 && report.Int("attempted") == 205 && report.GetProperty("results").GetArrayLength() == 205 && report.GetProperty("failures").GetArrayLength() == 0,
                "Catalog report omitted definitions, receipts or failures.");
            Check(full.Runtime.Calls.Where(c => c.Method == "clash.shop.catalog").Select(c => c.Args.Int("offset")).SequenceEqual(new[] { 0, 200 }), "Catalog skipped a page or ignored the final short page.");
            Check(full.Runtime.Calls.Count(c => c.Method == "time.step" && c.Args.Int("ticks") == 600) == 12 && full.Stocks["Silver"] == 796,
                "Catalog changed the 20-item batch, isolated Silver landing, final flush or currency conservation.");
            Check(full.Runtime.Calls.Where(c => c.Method == "clash.shop.catalogTemplate").GroupBy(c => (c.Client, c.Args.String("defName"))).All(g => g.Count() >= 2), "Catalog retained a preflight template lease for later purchases.");
        }
        foreach (string fault in new[] { "none", "funded", "limit", "duplicate-def", "duplicate-selection", "missing-selection", "incomplete-template", "listing-rejected", "listing-missing", "items", "stock", "cash" })
        {
            using var f = await Fixture.Create(fault);
            if (fault is "funded" or "limit") f.Stocks["Silver"] = 0;
            string[] selected = fault switch { "duplicate-selection" => ["Thing000", "Thing000"], "missing-selection" => ["absent"], _ => ["Thing000", "Silver", "Thing204"] };
            if (fault is not ("none" or "funded")) { await Reject(() => f.Catalog.Run(selected), fault); continue; }
            await f.Catalog.Run(selected);
            Check(f.Report().Int("total") == 3 && f.Attempts.Count == 3 && f.Loans == (fault == "funded" ? 1 : 0), "Sample ignored its exact selection or funded an already solvent buyer.");
        }
    }
    private static async Task StressAsync()
    {
        foreach (string fault in new[] { "none", "list-count", "listing-missing", "cash", "stock", "reopen-list", "reopen-buyers" })
        {
            using var f = await Fixture.Create(fault, durable: true);
            if (fault != "none") { await Reject(() => f.Stress.Run([25]), fault); continue; }
            await f.Stress.Run([25, 40]);
            var measurements = f.Report().GetProperty("measurements").EnumerateArray().ToArray();
            Check(f.Store.List().Count == 40 && f.Attempts.Count == 2 && f.Reopens == 2 && measurements.Select(m => m.Int("size")).SequenceEqual(new[] { 25, 40 }), "Stress reset its growing registry or omitted a measured size.");
            Check(measurements.All(m => m.Int("registryContenders") == 64 && m.Int("distinctRegistryPurchases") == 20 && m.GetProperty("refreshMs").GetArrayLength() == 3
                && m.GetProperty("clientWorkingSetBytes").EnumerateObject().Count() == 2 && m.GetProperty("registryJsonBytes").GetInt64() > 0), "Stress report lost contention, refresh, memory or payload measurements.");
            Check(f.Stocks["Silver"] == 998 && f.Stocks["Steel"] == 102, "Stress fixture bought instead of the client transaction or charged twice.");
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly GameScenarioTestRuntime Runtime;
        internal readonly ServerShopRegistry Store;
        internal readonly ShopScenario Shop;
        internal readonly ShopCatalogScenario Catalog;
        internal readonly ShopStressScenario Stress;
        internal readonly Dictionary<string, int> Stocks = new() { ["Silver"] = 1000, ["Steel"] = 100 };
        internal readonly List<int> Attempts = [];
        internal int Loans, Reopens;
        private readonly string root = Path.Combine(Path.GetTempPath(), "ShopScenarioUnit-" + Guid.NewGuid().ToString("N"));
        private readonly string fault;
        private readonly string? database;
        private readonly Dictionary<string, (string Def, int Count)> templates = [];
        private readonly Dictionary<(string Client, string Def), string> currentTemplates = [];
        private int serial;
        private string snapshot = "before";
        private string ReportPath => Path.Combine(root, "report.json");
        private Fixture(GameScenarioTestRuntime runtime, string fault, bool durable)
        {
            Runtime = runtime; this.fault = fault; Directory.CreateDirectory(root);
            if (durable) { database = Path.Combine(root, "shop.sqlite"); ServerDatabaseMigrator.Migrate(database); }
            Store = Open();
            var colony = new ColonyScenarioFixture(runtime.Run, runtime.Polling);
            var economy = new EconomicScenarioFixture(runtime.Run, runtime.Polling, colony);
            var actions = new ShopScenarioFixture(runtime.Run, runtime.Polling);
            Shop = new(runtime.Run, runtime.Polling, colony, economy, actions);
            Catalog = new(runtime.Run, runtime.Polling, colony, economy, actions, Store, ReportPath);
            Stress = new(runtime.Run, colony, actions, Store, Reopen, ReportPath);
            runtime.Script = Reply;
        }
        internal static async Task<Fixture> Create(string fault = "none", bool durable = false) => new(await GameScenarioTestRuntime.Create(), fault, durable);
        private ServerShopRegistry Open() => database is null ? new() : new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.ServerShop), null);
        internal JsonElement Report() => Wire.Element(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(ReportPath)));
        private Task<ShopPersistenceEvidence> Reopen(string id, IReadOnlyList<ProtocolIdentity> buyers)
        {
            Reopens++; var restored = Open();
            var counts = buyers.Select(b => restored.GetBuyerPurchaseCount(id, b)).ToArray();
            if (fault == "reopen-buyers") counts[0]++;
            return Task.FromResult(new ShopPersistenceEvidence(fault == "reopen-list" ? "[]" : JsonSerializer.Serialize(restored.List()), counts));
        }
        private object? Reply(string client, string method, JsonElement args)
        {
            switch (method)
            {
                case "clash.state": return new { currentSnapshotId = snapshot };
                case "runtime.hello": return new { processId = Environment.ProcessId };
                case "clash.inventory": return new { maps = new[] { new { items = Stocks } } };
                case "game.maps": return List(Reference("home"));
                case "designators.list": return List(new { reference = new { handle = "stockpile", type = "RimWorld.Designator_ZoneAddStockpile_Resources" } });
                case "map.things": return List();
                case "clash.shop.template":
                case "clash.shop.catalogTemplate":
                    string def = args.String("defName"), handle = client + "-template-" + serial++;
                    templates[handle] = (def, method == "clash.shop.template" ? args.Int("count") : 1);
                    currentTemplates[(client, def)] = handle;
                    return new { reference = new { handle }, complete = fault != "incomplete-template" };
                case "clash.shop.catalog":
                    Check(client == "A" && args.Int("limit") == 200, "Catalog changed observer/page size.");
                    var names = Enumerable.Range(0, 205).Select(i => i == 20 ? "Silver" : "Thing" + i.ToString("D3")).ToArray();
                    if (fault == "duplicate-def") names[204] = names[0];
                    return List(names.Skip(args.Int("offset")).Take(args.Int("limit")).Select(n => (object)new { defName = n }).ToArray());
                case "clash.shop.received": return new { matching = Stocks.GetValueOrDefault(TemplateValue(client, args.String("template")).Def) };
                case "clash.records": return List(Store.List().Select(l => Record(client, l)).ToArray());
                case "clash.shop.listing": return new { count = Store.List().Count + (fault == "list-count" ? -1 : 0), listing = fault == "listing-missing" ? null : Record(client, Store.Find(args.String("listingId"))!) };
                case "clash.bank": return new { minLoanSilver = 100, maxLoanSilver = fault == "limit" ? 50 : 1000, minDurationDays = 2 };
                case "clash.invoke": return Invoke(client, args);
                default: return null;
            }
        }
        private object Invoke(string client, JsonElement args)
        {
            var input = args.GetProperty("arguments");
            switch (args.String("action"))
            {
                case "StartRefreshServerShop": case "StartRefreshBankStatus": break;
                case "StartCreateBankLoan": Loans++; Stocks["Silver"] += input.Int("principalSilver"); break;
                case "StartUpsertServerShopListing":
                    Check(client == "A", "Shop listing used guest credentials.");
                    if (fault == "listing-rejected") break;
                    var item = TemplateValue(client, input.GetProperty("item").String("handle"));
                    Store.Upsert(input.OptionalString("listingId"), input.String("listingKind"), new ThingReferenceDto("item-" + serial++, item.Def, item.Count),
                        input.Int("priceSilver"), input.Int("stockCount"), 1, ServerShopQualityRequirementModes.AtLeast, ServerShopHitPointsRequirementModes.AtLeast, "rimtest-A", DateTimeOffset.UnixEpoch);
                    break;
                case "StartPurchaseServerShopListing":
                    Check(client == "B", "Shop bought through administrator.");
                    int count = input.Int("purchaseCount"); Attempts.Add(count);
                    var listing = Store.Find(ListingId(input, client))!;
                    var result = Store.TryPurchase("purchase-" + serial++, listing.ListingId, listing.ListingKind, listing.PriceSilver, listing.PriceSilver * count, count, new("rimtest-B", "colony", null), DateTimeOffset.UnixEpoch);
                    if (!result.Accepted)
                    {
                        if (fault == "excess-cash" && count == 2 || fault == "empty-cash" && count == 1) Stocks["Silver"]++;
                        if (fault == "invalid-snapshot") snapshot = "invalid";
                        break;
                    }
                    int direction = listing.ListingKind == "BuyFromPlayer" ? -1 : 1;
                    Stocks["Silver"] -= direction * listing.PriceSilver * count;
                    string itemDef = listing.Item.DefName!;
                    Stocks[itemDef] = Stocks.GetValueOrDefault(itemDef) + direction * listing.Item.StackCount * count + (fault == "items" ? 1 : 0);
                    if (fault == "cash") Stocks["Silver"]++;
                    if (fault == "stock") Store.Upsert(listing.ListingId, listing.ListingKind, listing.Item, listing.PriceSilver, 9, 1, listing.QualityRequirementMode, listing.HitPointsRequirementMode, "rimtest-A", DateTimeOffset.UnixEpoch);
                    break;
                case "StartRemoveServerShopListing": Check(client == "A", "Removal used buyer credentials."); Store.Remove(ListingId(input, client)); break;
                default: throw new InvalidOperationException("Unexpected shop action: " + args.String("action"));
            }
            return new { returnValue = true };
        }
        private static string ListingId(JsonElement input, string client)
        {
            string handle = input.GetProperty("listing").String("handle"); Check(handle.StartsWith(client + ":"), "Listing reference crossed clients."); return handle[(client.Length + 1)..];
        }
        private (string Def, int Count) TemplateValue(string client, string handle)
        {
            var value = templates[handle];
            Check(currentTemplates.GetValueOrDefault((client, value.Def)) == handle, "Template lease was stale or belonged to another client.");
            return value;
        }
        private static object Record(string client, ServerShopListingRecord listing) => new { reference = new { handle = client + ":" + listing.ListingId },
            data = new { listingId = listing.ListingId, listingKind = listing.ListingKind, stockCount = listing.StockCount, item = new { defName = listing.Item.DefName } } };
        public void Dispose() { Runtime.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
    private static object Reference(string handle) => new { reference = new { handle } };
    private static object List(params object[] items) => new { items };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string context)
    {
        try { await action(); } catch (AssertionException) { return; }
        throw new InvalidOperationException("Expected shop assertion: " + context);
    }
}
