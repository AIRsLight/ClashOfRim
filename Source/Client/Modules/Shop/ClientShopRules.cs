using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal static class ClientShopRules
{
    internal static string NormalizeListingKind(string? kind) => kind == ServerShopListingKinds.BuyFromPlayer
        ? ServerShopListingKinds.BuyFromPlayer : ServerShopListingKinds.SellToPlayer;
    internal static bool IsBuyOrder(ModServerShopListingDto listing) => NormalizeListingKind(listing.ListingKind) == ServerShopListingKinds.BuyFromPlayer;
    internal static int BasePrice(ModServerShopListingDto listing) => listing.BasePriceSilver > 0 ? listing.BasePriceSilver : listing.PriceSilver;
    internal static int TotalPrice(ModServerShopListingDto listing, int count) => ServerShopPricing.TotalPrice(
        BasePrice(listing), listing.PriceIncreaseRatio, Math.Max(0, listing.BuyerPurchaseCount), count);
}
