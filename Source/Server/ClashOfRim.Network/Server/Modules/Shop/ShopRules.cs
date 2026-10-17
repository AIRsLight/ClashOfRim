using AIRsLight.ClashOfRim.Protocol;
using System.Globalization;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Shop;

internal static class ShopRules
{
    internal static ProtocolResponse? ValidateListing(ThingReferenceDto? item, string kind, int price, int stock)
    {
        if (!ServerShopListingKinds.IsKnown(kind)) return Reject("Shop.InvalidKind");
        if (item is null || string.IsNullOrWhiteSpace(item.GlobalKey) || string.IsNullOrWhiteSpace(item.DefName) || item.StackCount <= 0)
            return Reject("Shop.InvalidItem");
        if (item.PawnPackage is not null || !string.IsNullOrWhiteSpace(item.PawnPackageId)) return Reject("Shop.PawnUnsupported");
        if (price < 1) return Reject("Shop.InvalidPrice");
        return stock < 0 ? Reject("Shop.InvalidStock") : null;
    }
    private static ProtocolResponse Reject(string key) => ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format(key));

    // Both listing normalization and quantity expansion preserve the same complex item fields.
    // Callers validate positive counts before entering these transformations.
    internal static ThingReferenceDto NormalizeItem(ThingReferenceDto item) => CopyItem(item, item.StackCount, normalize: true);
    internal static ThingReferenceDto? MultiplyRequirement(ThingReferenceDto item, int count)
    {
        long total = (long)Math.Max(1, item.StackCount) * Math.Max(1, count);
        return total > int.MaxValue ? null : CopyItem(item, (int)total, normalize: false);
    }
    private static ThingReferenceDto CopyItem(ThingReferenceDto item, int count, bool normalize) => new(
        item.GlobalKey, item.DefName, count, item.Quality, item.HitPoints, item.MinifiedInnerDefName,
        item.MinifiedInnerStuffDefName, item.MinifiedInnerQuality, item.MinifiedInnerHitPoints, item.WornByCorpse,
        item.Biocoded, item.BiocodedPawnLabel, item.BiocodedPawnGlobalId, item.DisplayLabel, normalize ? null : item.MarketValue,
        item.UniqueWeapon, item.UniqueWeaponTraits, normalize ? null : item.PawnPackage, normalize ? null : item.PawnPackageId,
        item.StuffDefName, item.MaxHitPoints, item.MinifiedInnerMaxHitPoints, metadata: CopyMetadata(item.Metadata),
        thingPackage: item.ThingPackage, thingPackageId: item.ThingPackageId);

    internal static ServerShopListingDto ToDto(ServerShopListingRecord listing, ServerShopRegistry shop, ProtocolIdentity viewer)
    {
        int purchases = shop.GetBuyerPurchaseCount(listing.ListingId, viewer);
        return new(listing.ListingId, listing.ListingKind, listing.Item, ServerShopRegistry.CalculateCurrentUnitPrice(listing, purchases),
            listing.StockCount, listing.CreatedAtUtc, listing.UpdatedAtUtc, listing.UpdatedByUserId, listing.PriceSilver,
            listing.PriceIncreaseRatio, purchases, listing.QualityRequirementMode, listing.HitPointsRequirementMode);
    }

    internal static ProtocolResponse PurchaseRejection(ServerShopPurchaseResult purchase, PurchaseServerShopListingRequest request) =>
        ProtocolResponse.Reject(purchase.FailureKey == "Shop.PriceChanged" ? ProtocolErrorCode.Conflict : ProtocolErrorCode.ServerRejected,
            Format(purchase.FailureKey ?? "Shop.PurchaseRejected",
                ("PRICE", request.UnitPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("EXPECTED", purchase.CurrentUnitPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("TOTAL", request.TotalPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("EXPECTEDTOTAL", purchase.ExpectedTotalPriceSilver.ToString(CultureInfo.InvariantCulture)),
                ("COUNT", request.PurchaseCount.ToString(CultureInfo.InvariantCulture)),
                ("STOCK", purchase.RemainingStockCount.ToString(CultureInfo.InvariantCulture))));
}
