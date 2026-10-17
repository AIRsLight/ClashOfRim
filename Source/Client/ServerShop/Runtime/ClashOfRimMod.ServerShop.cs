using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim;

// Compatibility entry points for existing UI/RimTest callers; retired with M11.
public sealed partial class ClashOfRimMod
{
    internal void StartRefreshServerShop() => _ = shopCatalog.RefreshAsync();

    internal void StartUpsertServerShopListing(string? listingId, string listingKind, ModThingReferenceDto item,
        int priceSilver, int stockCount, double priceIncreaseRatio = 1d,
        string qualityRequirementMode = "AtLeast", string hitPointsRequirementMode = "AtLeast") =>
        _ = shopCatalog.UpsertAsync(new(listingId, listingKind, item, priceSilver, stockCount,
            priceIncreaseRatio, qualityRequirementMode, hitPointsRequirementMode));

    internal void StartRemoveServerShopListing(ModServerShopListingDto listing) => _ = shopCatalog.RemoveAsync(listing.ListingId);

    internal void StartPurchaseServerShopListing(ModServerShopListingDto listing, int purchaseCount) => _ = shopOperations.StartAsync(listing, purchaseCount);

    internal static int CalculateServerShopTotalPrice(ModServerShopListingDto listing, int purchaseCount) => ClientShopRules.TotalPrice(listing, purchaseCount);
    internal static int CalculateServerShopUnitPrice(int basePriceSilver, double priceIncreaseRatio, int buyerPurchaseCount) => ServerShopPricing.UnitPrice(basePriceSilver, priceIncreaseRatio, buyerPurchaseCount);
    internal static double NormalizeServerShopPriceIncreaseRatio(double value) => ServerShopPricing.NormalizeRatio(value);
}
