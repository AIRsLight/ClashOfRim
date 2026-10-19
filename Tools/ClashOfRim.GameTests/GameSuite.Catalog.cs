using System.Diagnostics;
using System.Text.Json;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task ShopCatalog()
    {
        var definitions = new List<JsonElement>();
        for (int offset = 0; ; offset += 200)
        {
            var page = Items(await Call("A", "clash.shop.catalog", new { offset, limit = 200 }));
            definitions.AddRange(page); if (page.Length < 200) break;
        }
        Check(definitions.Count > 100 && definitions.Select(d => d.String("defName")).Distinct().Count() == definitions.Count, "Shop catalog enumeration was incomplete/duplicated.");
        if (profile.CatalogDefinitions is { Length: > 0 } selected)
        {
            Check(selected.Distinct(StringComparer.Ordinal).Count() == selected.Length, "Sample contains duplicate definitions.");
            definitions = definitions.Where(d => selected.Contains(d.String("defName"), StringComparer.Ordinal)).ToList();
            Check(definitions.Count == selected.Length, "Sample contains a definition not offered by the editor.");
        }
        var failures = new List<string>();
        async Task<JsonElement> Template(string client, string name)
        {
            var value = await Call(client, "clash.shop.catalogTemplate", new { defName = name, variant = true });
            Check(value.Bool("complete"), "Original editor still requires metadata: " + name);
            return value;
        }
        foreach (var def in definitions)
        {
            string name = def.String("defName");
            await Template("A", name); await Template("B", name);
        }
        // Validate every editor configuration before starting the much slower purchase loop.
        Console.WriteLine($"CATALOG templates ready: {definitions.Count}");
        int cash = await colonyFixture.Stock("B", "Silver");
        if (cash < definitions.Count + 50)
        {
            await Invoke("B", "StartRefreshBankStatus"); await Idle("B");
            var bank = await Call("B", "clash.bank");
            int principal = Math.Max(bank.Int("minLoanSilver"), definitions.Count + 50 - cash);
            Check(principal <= bank.Int("maxLoanSilver"), "Catalog fixture cannot fund all purchases through the bank.");
            await Invoke("B", "StartCreateBankLoan", new { principalSilver = principal, durationDays = bank.Int("minDurationDays") });
            await Idle("B"); await colonyFixture.AwaitStock("B", "Silver", cash + principal);
        }
        var results = new List<object>();
        var pending = new List<(string Def, string Id, int Before, Stopwatch Watch)>();
        int initialSilver = await colonyFixture.Stock("B", "Silver"), bought = 0, silverReceived = 0;
        async Task LandBatch()
        {
            if (pending.Count == 0) return;
            await Step("B", 600); await Idle("B");
            foreach (var p in pending)
            {
                var received = await Call("B", "clash.shop.received", new { template = H(await Template("B", p.Def)) });
                bool matched = received.Int("matching") == p.Before + (p.Def == "Silver" ? 0 : 1);
                if (!matched) failures.Add(p.Def + ": delivered state/count mismatch " + received.GetRawText());
                var listing = ServerState.ServerShop.Find(p.Id)!;
                if (listing.StockCount != 1) failures.Add(p.Def + ": stock was not decremented once");
                results.Add(new { defName = p.Def, listingId = p.Id, matched, stock = listing.StockCount, elapsedMs = p.Watch.ElapsedMilliseconds, received });
            }
            pending.Clear();
            await File.WriteAllTextAsync(Path.Combine(root, "shop-catalog.json"), JsonSerializer.Serialize(new { total = definitions.Count, attempted = bought, results, failures }, Wire.Json));
        }
        foreach (var definition in definitions)
        {
            string name = definition.String("defName"), id = "catalog-" + name;
            // Silver is both payment and product. Isolate its landing from other payments.
            if (name == "Silver") await LandBatch();
            var watch = Stopwatch.StartNew();
            // Object references have a bounded lease; reacquire at each use, not once per catalog.
            var seller = await Template("A", name);
            string templateHandle = H(await Template("B", name));
            int before = (await Call("B", "clash.shop.received", new { template = templateHandle })).Int("matching");
            await Invoke("A", "StartUpsertServerShopListing", new { listingId = id, listingKind = "SellToPlayer", item = seller.GetProperty("reference"), priceSilver = 1, stockCount = 2 });
            await Idle("A");
            if (ServerState.ServerShop.Find(id) is null) { failures.Add(name + ": listing rejected"); continue; }
            await Invoke("B", "StartRefreshServerShop"); await Idle("B");
            var listing = (await Call("B", "clash.shop.listing", new { listingId = id })).GetProperty("listing");
            Check(listing.ValueKind == JsonValueKind.Object, "Published catalog listing missing on buyer: " + name);
            await Invoke("B", "StartPurchaseServerShopListing", new { listing = listing.GetProperty("reference"), purchaseCount = 1 });
            await Idle("B"); bought++;
            if (name == "Silver") silverReceived++;
            pending.Add((name, id, before, watch));
            if (name == "Silver") await LandBatch();
            if (pending.Count == 20) { Console.WriteLine($"CATALOG {bought}/{definitions.Count}"); await LandBatch(); }
        }
        await LandBatch();
        Check(await colonyFixture.Stock("B", "Silver") == initialSilver - bought + silverReceived, "Catalog purchases did not conserve silver.");
        Check(failures.Count == 0, "Catalog failures: " + string.Join("; ", failures));
        Check(bought == definitions.Count, "Not all offered definitions were purchased.");
    }
}
