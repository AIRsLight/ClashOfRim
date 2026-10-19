using System.Text.Json;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ShopScenarioFixture(TestRun run, ScenarioPolling polling)
{
    internal async Task Upsert(string client, string? id, string kind, JsonElement item, int price, int stock)
    {
        await run.InvokeClientAsync(client, "StartUpsertServerShopListing", new { listingId = id, listingKind = kind, item, priceSilver = price, stockCount = stock });
        await polling.Idle(client);
    }
    internal async Task Refresh(string client)
    {
        await run.InvokeClientAsync(client, "StartRefreshServerShop"); await polling.Idle(client);
    }
    internal Task<JsonElement> AttemptPurchase(string client, JsonElement listing, int count = 1) =>
        run.InvokeClientAsync(client, "StartPurchaseServerShopListing", new { listing = listing.GetProperty("reference"), purchaseCount = count });
    internal async Task Purchase(string client, JsonElement listing, int count = 1)
    {
        await AttemptPurchase(client, listing, count); await polling.Idle(client);
    }
    internal Task<JsonElement> Remove(string client, JsonElement listing) =>
        run.InvokeClientAsync(client, "StartRemoveServerShopListing", new { listing = listing.GetProperty("reference") });
}
