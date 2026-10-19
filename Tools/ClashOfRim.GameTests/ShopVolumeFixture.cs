using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

namespace ClashOfRim.GameTests;

// Direct registry seeding/contention is an explicit fixture, separate from client snapshot purchases.
internal sealed class ShopVolumeFixture(ServerShopRegistry shop)
{
    internal long Populate(int populated, int size, DateTimeOffset now)
    {
        for (int i = populated; i < size; i++)
            shop.Upsert("stress-" + i.ToString("D6"), ServerShopListingKinds.SellToPlayer,
                new ThingReferenceDto("stress-item-" + i, "Steel", 1, displayLabel: "压力商品 / stress " + (i % 37)),
                1, 3, 1, ServerShopQualityRequirementModes.AtLeast, ServerShopHitPointsRequirementModes.AtLeast, "rimtest-A", now);
        var listings = shop.List();
        Check(listings.Count == size && listings.Select(l => l.ListingId).Distinct().Count() == size, "Volume fixture lost/duplicated rows.");
        return JsonSerializer.SerializeToUtf8Bytes(listings).LongLength;
    }
    internal async Task<ServerShopPurchaseResult[]> Contend(int size, DateTimeOffset now)
    {
        const string id = "stress-000000";
        shop.Upsert(id, ServerShopListingKinds.SellToPlayer, new ThingReferenceDto("race-item", "Steel", 1), 1, 20, 1,
            ServerShopQualityRequirementModes.AtLeast, ServerShopHitPointsRequirementModes.AtLeast, "rimtest-A", now);
        var races = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => shop.TryPurchase(
            "stress-race-" + size + "-" + (i / 2), id, ServerShopListingKinds.SellToPlayer, 1, 1, 1,
            new ProtocolIdentity("stress-buyer-" + (i / 2), "colony", null), now))));
        Check(races.Count(r => r.Accepted && !r.Duplicate) == 20 && shop.Find(id)!.StockCount == 0, "Concurrent fixture oversold stock or replayed a completed purchase.");
        return races;
    }
}
