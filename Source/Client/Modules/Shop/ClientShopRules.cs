using System;
using System.Globalization;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal static class ClientShopRules
{
    internal static string NormalizeListingKind(string? kind) => kind == ServerShopListingKinds.BuyFromPlayer
        ? ServerShopListingKinds.BuyFromPlayer : ServerShopListingKinds.SellToPlayer;
    internal static double DefaultPriceRatio(string kind) => kind == ServerShopListingKinds.BuyFromPlayer ? 0.95d : 1.05d;
    internal static bool IsBuyOrder(ModServerShopListingDto listing) => NormalizeListingKind(listing.ListingKind) == ServerShopListingKinds.BuyFromPlayer;
    internal static int BasePrice(ModServerShopListingDto listing) => listing.BasePriceSilver > 0 ? listing.BasePriceSilver : listing.PriceSilver;
    internal static string UnitPriceText(ModServerShopListingDto listing) =>
        (BasePrice(listing) / (decimal)Math.Max(1, listing.Item?.StackCount ?? 1)).ToString("G29", CultureInfo.InvariantCulture);

    // The protocol stores whole silver per lot. Keep both editing surfaces on the
    // same conversion, rounding once after multiplication (including fractional units).
    internal static bool TryLotPrice(string text, int count, CultureInfo inputCulture, out int price)
    {
        price = 0;
        if (count < 1 || !(decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal unitPrice)
                || decimal.TryParse(text, NumberStyles.Float, inputCulture, out unitPrice))
            || unitPrice <= 0 || unitPrice > (int.MaxValue + 0.5m) / count) return false;
        decimal total = decimal.Round(unitPrice * count, 0, MidpointRounding.ToEven);
        if (total > int.MaxValue) return false;
        price = (int)Math.Max(1m, total);
        return true;
    }
    internal static int TotalPrice(ModServerShopListingDto listing, int count) => ServerShopPricing.TotalPrice(
        BasePrice(listing), listing.PriceIncreaseRatio, Math.Max(0, listing.BuyerPurchaseCount), count);
}
