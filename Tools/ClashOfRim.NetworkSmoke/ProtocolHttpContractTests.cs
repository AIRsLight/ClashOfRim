using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;

internal static class ProtocolHttpContractTests
{
    public static void Run()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var original = new ServerShopListingDto("listing", ServerShopListingKinds.SellToPlayer,
            new ThingReferenceDto("item", "Silver", 1), 75, 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            "admin", basePriceSilver: 50, priceIncreaseRatio: 1.5, buyerPurchaseCount: 1);
        var node = JsonSerializer.SerializeToNode(original, options)!;
        var restored = node.Deserialize<ServerShopListingDto>(options)!;
        Require(restored.BasePriceSilver == 50 && restored.PriceSilver == 75 && restored.PriceIncreaseRatio == 1.5
            && restored.BuyerPurchaseCount == 1 && restored.StockCount == 3, "Shop HTTP contract lost non-default pricing fields.");
        node.AsObject().Remove("basePriceSilver");
        Require(node.Deserialize<ServerShopListingDto>(options)!.BasePriceSilver == 75, "Missing base price must fall back to the current price.");
        node["basePriceSilver"] = null;
        Require(node.Deserialize<ServerShopListingDto>(options)!.BasePriceSilver == 75, "Null legacy base price must retain fallback semantics.");
        node["basePriceSilver"] = 0;
        Require(node.Deserialize<ServerShopListingDto>(options)!.BasePriceSilver == 0, "An explicit zero base price must not become a missing value.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
