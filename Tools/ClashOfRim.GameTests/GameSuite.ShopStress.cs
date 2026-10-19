using System.Diagnostics;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task ShopStress()
    {
        var measurements = new List<object>();
        int populated = 0;
        var shop = ServerState.ServerShop;
        var now = DateTimeOffset.UtcNow;
        foreach (int size in new[] { 1000, 10000, 50000 })
        {
            var populate = Stopwatch.StartNew();
            // Explicit volume fixture: use the actual persisted registry; no direct SQL or ledger edits.
            for (int i = populated; i < size; i++)
                shop.Upsert("stress-" + i.ToString("D6"), ServerShopListingKinds.SellToPlayer,
                    new ThingReferenceDto("stress-item-" + i, "Steel", 1, displayLabel: "压力商品 / stress " + (i % 37)),
                    1, 3, 1, ServerShopQualityRequirementModes.AtLeast, ServerShopHitPointsRequirementModes.AtLeast, "rimtest-A", now);
            populated = size; populate.Stop();
            await run.RecordAsync(new { fixture = "persisted-shop-volume", size, populateMs = populate.Elapsed.TotalMilliseconds });
            Check(shop.List().Count == size && shop.List().Select(l => l.ListingId).Distinct().Count() == size, "Volume fixture lost/duplicated rows.");
            long registryJsonBytes = JsonSerializer.SerializeToUtf8Bytes(shop.List()).LongLength;
            var queryTimes = new List<double>();
            foreach (string client in new[] { "A", "B", "B" })
            {
                var timer = Stopwatch.StartNew();
                await Invoke(client, "StartRefreshServerShop"); await Idle(client);
                var listing = await Call(client, "clash.shop.listing", new { listingId = "stress-" + (size - 1).ToString("D6") });
                Check(listing.Int("count") == size && listing.GetProperty("listing").ValueKind == JsonValueKind.Object, "Large catalog did not arrive completely: " + size);
                queryTimes.Add(timer.Elapsed.TotalMilliseconds);
            }
            // Exercise a real authenticated client snapshot purchase while the entire catalog is cached.
            int steel = await colonyFixture.Stock("B", "Steel"), silver = await colonyFixture.Stock("B", "Silver");
            string selected = "stress-" + (size - 1).ToString("D6");
            var record = (await Call("B", "clash.shop.listing", new { listingId = selected })).GetProperty("listing");
            var purchase = Stopwatch.StartNew();
            await Invoke("B", "StartPurchaseServerShopListing", new { listing = record.GetProperty("reference"), purchaseCount = 1 });
            await Idle("B"); await colonyFixture.AwaitStock("B", "Steel", steel + 1);
            Check(await colonyFixture.Stock("B", "Silver") == silver - 1 && shop.Find(selected)!.StockCount == 2, "Large-catalog purchase violated balances.");
            purchase.Stop();
            // Registry contention/replay is separate from the preceding live snapshot transaction.
            string raceId = "stress-000000";
            shop.Upsert(raceId, ServerShopListingKinds.SellToPlayer, new ThingReferenceDto("race-item", "Steel", 1), 1, 20, 1,
                ServerShopQualityRequirementModes.AtLeast, ServerShopHitPointsRequirementModes.AtLeast, "rimtest-A", now);
            var races = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => shop.TryPurchase(
                "stress-race-" + size + "-" + (i / 2), raceId, ServerShopListingKinds.SellToPlayer, 1, 1, 1,
                new ProtocolIdentity("stress-buyer-" + (i / 2), "colony", null), now))));
            Check(races.Count(r => r.Accepted && !r.Duplicate) == 20 && shop.Find(raceId)!.StockCount == 0, "Concurrent fixture oversold stock or replayed a completed purchase.");
            var listBefore = JsonSerializer.Serialize(shop.List());
            var reopen = Stopwatch.StartNew();
            await using (var reopened = ClashOfRimNetworkServer.Build([
                "--contentRoot", Path.Combine(root, "Server"), "--urls", "http://127.0.0.1:0",
                "--Persistence:DataDirectory", Path.Combine(root, "Server", "Data"), "--Authentication:DebugMode", "false",
                "--Localization:Language", "English", "--Logging:LogLevel:Default", "Warning"]))
            {
                // Open a fresh registry from the same durable database without starting another listener.
                var restored = reopened.Services.GetRequiredService<ClashOfRimNetworkState>().ServerShop;
                Check(JsonSerializer.Serialize(restored.List()) == listBefore, "Large catalog changed on durable reopen.");
                foreach (int i in Enumerable.Range(0, 32))
                {
                    var buyer = new ProtocolIdentity("stress-buyer-" + i, "colony", null);
                    Check(restored.GetBuyerPurchaseCount(raceId, buyer) == shop.GetBuyerPurchaseCount(raceId, buyer), "Reopen lost per-buyer history.");
                }
            }
            reopen.Stop();
            var memory = new Dictionary<string, long>();
            foreach (string client in clients.Keys)
            {
                int pid = (await Call(client, "runtime.hello")).Int("processId");
                using var process = Process.GetProcessById(pid); memory[client] = process.WorkingSet64;
            }
            measurements.Add(new { size, populateMs = populate.Elapsed.TotalMilliseconds, registryJsonBytes, refreshMs = queryTimes,
                purchaseMs = purchase.Elapsed.TotalMilliseconds, reopenMs = reopen.Elapsed.TotalMilliseconds, clientWorkingSetBytes = memory,
                registryContenders = races.Length, distinctRegistryPurchases = races.Count(r => r.Accepted && !r.Duplicate) });
            await File.WriteAllTextAsync(Path.Combine(root, "shop-stress.json"), JsonSerializer.Serialize(new { scope = "SQLite registry fixture plus live full-list refresh and purchase", measurements }, Wire.Json));
            Console.WriteLine($"STRESS {size}: refresh {string.Join('/', queryTimes.Select(t => t.ToString("F0")))} ms, purchase {purchase.ElapsedMilliseconds} ms");
        }
    }
}
