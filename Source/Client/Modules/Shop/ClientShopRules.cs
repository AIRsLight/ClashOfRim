using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal static class ClientShopRules
{
    internal static string NormalizeListingKind(string? kind) => kind == ServerShopListingKinds.BuyFromPlayer
        ? ServerShopListingKinds.BuyFromPlayer : ServerShopListingKinds.SellToPlayer;
    internal static bool IsBuyOrder(ModServerShopListingDto listing) => NormalizeListingKind(listing.ListingKind) == ServerShopListingKinds.BuyFromPlayer;
    internal static int TotalPrice(ModServerShopListingDto listing, int count) => ServerShopPricing.TotalPrice(
        listing.BasePriceSilver > 0 ? listing.BasePriceSilver : listing.PriceSilver, listing.PriceIncreaseRatio, Math.Max(0, listing.BuyerPurchaseCount), count);
}
