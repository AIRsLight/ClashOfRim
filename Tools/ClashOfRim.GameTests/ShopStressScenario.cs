using System.Diagnostics;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ShopStressScenario(TestRun run, ColonyScenarioFixture colonyFixture,
    ShopScenarioFixture actions, ServerShopRegistry shop,
    Func<string, IReadOnlyList<ProtocolIdentity>, Task<ShopPersistenceEvidence>> readPersisted, string reportPath)
{
    internal Task Run() => Run([1000, 10000, 50000]);
    internal async Task Run(IReadOnlyList<int> sizes)
    {
        var measurements = new List<object>();
        int populated = 0;
        var volume = new ShopVolumeFixture(shop);
        var now = DateTimeOffset.UtcNow;
        foreach (int size in sizes)
        {
            var populate = Stopwatch.StartNew();
            long registryJsonBytes = volume.Populate(populated, size, now);
            populated = size; populate.Stop();
            await run.RecordAsync(new { fixture = "persisted-shop-volume", size, populateMs = populate.Elapsed.TotalMilliseconds });
            var queryTimes = new List<double>();
            foreach (string client in new[] { "A", "B", "B" })
            {
                var timer = Stopwatch.StartNew();
                await actions.Refresh(client);
                var listing = await run.CallAsync(client, "clash.shop.listing", new { listingId = "stress-" + (size - 1).ToString("D6") });
                Check(listing.Int("count") == size && listing.GetProperty("listing").ValueKind == JsonValueKind.Object, "Large catalog did not arrive completely: " + size);
                queryTimes.Add(timer.Elapsed.TotalMilliseconds);
            }
            // Exercise a real authenticated client snapshot purchase while the entire catalog is cached.
            int steel = await colonyFixture.Stock("B", "Steel"), silver = await colonyFixture.Stock("B", "Silver");
            string selected = "stress-" + (size - 1).ToString("D6");
            var record = (await run.CallAsync("B", "clash.shop.listing", new { listingId = selected })).GetProperty("listing");
            var purchase = Stopwatch.StartNew();
            await actions.Purchase("B", record, 1);
            await colonyFixture.AwaitStock("B", "Steel", steel + 1);
            Check(await colonyFixture.Stock("B", "Silver") == silver - 1 && shop.Find(selected)!.StockCount == 2, "Large-catalog purchase violated balances.");
            purchase.Stop();
            // Registry contention/replay is separate from the preceding live snapshot transaction.
            string raceId = "stress-000000";
            var races = await volume.Contend(size, now);
            var listBefore = JsonSerializer.Serialize(shop.List());
            var reopen = Stopwatch.StartNew();
            var buyers = Enumerable.Range(0, 32).Select(i => new ProtocolIdentity("stress-buyer-" + i, "colony", null)).ToArray();
            var restored = await readPersisted(raceId, buyers);
            Check(restored.ListingsJson == listBefore, "Large catalog changed on durable reopen.");
            Check(restored.BuyerPurchaseCounts.SequenceEqual(buyers.Select(buyer => shop.GetBuyerPurchaseCount(raceId, buyer))), "Reopen lost per-buyer history.");
            reopen.Stop();
            var memory = new Dictionary<string, long>();
            foreach (string client in new[] { "A", "B" })
            {
                int pid = (await run.CallAsync(client, "runtime.hello")).Int("processId");
                using var process = Process.GetProcessById(pid); memory[client] = process.WorkingSet64;
            }
            measurements.Add(new { size, populateMs = populate.Elapsed.TotalMilliseconds, registryJsonBytes, refreshMs = queryTimes,
                purchaseMs = purchase.Elapsed.TotalMilliseconds, reopenMs = reopen.Elapsed.TotalMilliseconds, clientWorkingSetBytes = memory,
                registryContenders = races.Length, distinctRegistryPurchases = races.Count(r => r.Accepted && !r.Duplicate) });
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { scope = "SQLite registry fixture plus live full-list refresh and purchase", measurements }, Wire.Json));
            Console.WriteLine($"STRESS {size}: refresh {string.Join('/', queryTimes.Select(t => t.ToString("F0")))} ms, purchase {purchase.ElapsedMilliseconds} ms");
        }
    }
}
