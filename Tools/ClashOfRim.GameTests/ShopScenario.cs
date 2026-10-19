using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ShopScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colonyFixture,
    EconomicScenarioFixture economy, ShopScenarioFixture actions)
{
    internal Task Purchase() => Purchase(preserveListing: false);
    internal async Task Purchase(bool preserveListing)
    {
        await polling.Idle("A");
        var template = await run.CallAsync("A", "clash.shop.template", new { defName = "Steel", count = 5 });
        await actions.Upsert("A", (string?)null, "SellToPlayer", template.GetProperty("reference"), 10, 3);
        await actions.Refresh("B");
        var shop = await polling.Wait("B", "clash.records", new { kind = "shop" }, s => Items(s).Any(v => v.GetProperty("data").GetProperty("item").String("defName") == "Steel"));
        var listing = Items(shop).Single(v => v.GetProperty("data").GetProperty("item").String("defName") == "Steel");
        int steel = await colonyFixture.Stock("B", "Steel"), silver = await colonyFixture.Stock("B", "Silver");
        Check(silver >= 10, "Purchase fixture lacks landed silver.");
        await actions.Purchase("B", listing, 1);
        await polling.Step("B", 600);
        int actualSteel = await colonyFixture.Stock("B", "Steel"), actualSilver = await colonyFixture.Stock("B", "Silver");
        Check(actualSteel == steel + 5 && actualSilver == silver - 10, $"Shop stock mismatch: steel {steel} -> {actualSteel}, silver {silver} -> {actualSilver}.");
        await actions.Refresh("A");
        shop = await polling.Wait("A", "clash.records", new { kind = "shop" }, s => Items(s).Any(v => v.GetProperty("data").String("listingId") == listing.GetProperty("data").String("listingId") && v.GetProperty("data").Int("stockCount") == 2));
        var refreshed = Items(shop).Single(v => v.GetProperty("data").String("listingId") == listing.GetProperty("data").String("listingId"));
        if (preserveListing) return;
        await actions.Remove("A", refreshed);
        await polling.Wait("A", "clash.records", new { kind = "shop" }, s => !Items(s).Any(v => v.GetProperty("data").String("listingId") == listing.GetProperty("data").String("listingId")));
    }
    internal async Task Buyback()
    {
        await colonyFixture.StoreStartingResources("B");
        var template = await run.CallAsync("A", "clash.shop.template", new { defName = "Steel", count = 5 });
        await actions.Upsert("A", (string?)null, "BuyFromPlayer", template.GetProperty("reference"), 10, 1);
        await actions.Refresh("B");
        var shop = await polling.Wait("B", "clash.records", new { kind = "shop" }, s => Items(s).Any(v => v.GetProperty("data").OptionalString("listingKind") == "BuyFromPlayer"));
        var listing = Items(shop).Single(v => v.GetProperty("data").OptionalString("listingKind") == "BuyFromPlayer");
        string id = listing.GetProperty("data").String("listingId");
        await economy.AssertNoMutation("B", () => actions.AttemptPurchase("B", listing, 2));
        int silver = await colonyFixture.Stock("B", "Silver"), steel = await colonyFixture.Stock("B", "Steel");
        await actions.Purchase("B", listing, 1);
        await colonyFixture.AwaitStock("B", "Silver", silver + 10);
        Check(await colonyFixture.Stock("B", "Steel") == steel - 5, "Buyback did not remove exact items.");
        await actions.Refresh("B");
        var exhausted = await polling.Wait("B", "clash.records", new { kind = "shop" }, p => Items(p).Any(v => v.GetProperty("data").String("listingId") == id && v.GetProperty("data").Int("stockCount") == 0));
        listing = Items(exhausted).Single(v => v.GetProperty("data").String("listingId") == id);
        await economy.AssertNoMutation("B", () => actions.AttemptPurchase("B", listing, 1));
        await actions.Refresh("A");
        var current = Items(await run.CallAsync("A", "clash.records", new { kind = "shop" })).Single(v => v.GetProperty("data").String("listingId") == id);
        Check(current.GetProperty("data").Int("stockCount") == 0, "Buyback stock not exhausted.");
        await actions.Remove("A", current); await polling.Idle("A");
    }
}
