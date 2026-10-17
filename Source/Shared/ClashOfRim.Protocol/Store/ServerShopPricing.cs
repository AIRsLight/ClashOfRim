using System;

namespace AIRsLight.ClashOfRim.Protocol;

// Shared by the authoritative registry and client quotes; round each unit before summing.
public static class ServerShopPricing
{
    public static double NormalizeRatio(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || value <= 0d ? 1d : Math.Max(0.01d, Math.Min(value, 100d));

    public static int UnitPrice(int basePriceSilver, double ratio, long buyerPurchaseCount)
    {
        int basePrice = Math.Max(1, basePriceSilver);
        if (buyerPurchaseCount <= 0) return basePrice;
        double multiplier = NormalizeRatio(ratio);
        if (multiplier == 1d) return basePrice;
        double calculated = basePrice * Math.Pow(multiplier, buyerPurchaseCount);
        if (double.IsNaN(calculated) || double.IsInfinity(calculated) || calculated >= int.MaxValue) return int.MaxValue;
        int rounded = (int)Math.Ceiling(calculated);
        return multiplier > 1d ? Math.Max(basePrice, rounded) : Math.Max(1, Math.Min(basePrice, rounded));
    }

    public static int TotalPrice(int basePriceSilver, double ratio, int buyerPurchaseCount, int purchaseCount)
    {
        int count = Math.Max(1, purchaseCount);
        double multiplier = NormalizeRatio(ratio);
        if (multiplier == 1d) return (int)Math.Min(int.MaxValue, (long)Math.Max(1, basePriceSilver) * count);
        long total = 0;
        for (int offset = 0; offset < count; offset++)
        {
            // The lifetime purchase count may already be near int.MaxValue.
            int price = UnitPrice(basePriceSilver, multiplier, (long)buyerPurchaseCount + offset);
            // A decreasing price cannot leave the minimum once it has reached it.
            if (multiplier < 1d && price == 1) return (int)Math.Min(int.MaxValue, total + count - offset);
            total += price;
            if (total >= int.MaxValue) return int.MaxValue;
        }
        return (int)total;
    }
}
