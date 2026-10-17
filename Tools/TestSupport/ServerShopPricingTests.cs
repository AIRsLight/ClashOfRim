using System;
using System.Globalization;
using AIRsLight.ClashOfRim.Protocol;

internal static class ServerShopPricingTests
{
    internal static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var sample in new (int Price, double Ratio, long Bought, int Expected)[] {
                    (10, 1.5, 0, 10), (10, 1.5, 1, 15), (10, 1.5, 2, 23), (10, 1.5, 3, 34),
                    (10, .5, 1, 5), (10, .5, 2, 3), (10, .5, 3, 2), (10, .5, 4, 1),
                    (0, 1, 0, 1), (-5, 2, -1, 1), (2, 1000, 1, 200), (100, .001, 1, 1),
                    (10, 1.000000001, int.MaxValue, 86), (10, 1.000000001, (long)int.MaxValue + 2, 86), (10, 100, 20, int.MaxValue) })
                    Check(ServerShopPricing.UnitPrice(sample.Price, sample.Ratio, sample.Bought) == sample.Expected, "Unit price rounding, bounds or lifetime count changed.");
                foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0d })
                    Check(ServerShopPricing.NormalizeRatio(invalid) == 1 && ServerShopPricing.TotalPrice(10, invalid, 10, 5) == 50, "Invalid multiplier did not fall back to fixed pricing.");
                Check(ServerShopPricing.TotalPrice(10, 1.5, 0, 4) == 82 && ServerShopPricing.TotalPrice(10, .5, 0, 5) == 21,
                    "Total price stopped rounding each unit independently.");
                Check(ServerShopPricing.TotalPrice(10, 1.000000001, int.MaxValue - 1, 3) == 258, "Lifetime count overflow reset later unit prices.");
                Check(ServerShopPricing.TotalPrice(10, 1.5, 0, 0) == 10, "Legacy minimum purchase count changed.");
                Check(ServerShopPricing.TotalPrice(1, 1, 0, int.MaxValue) == int.MaxValue
                    && ServerShopPricing.TotalPrice(2, .5, 0, int.MaxValue) == int.MaxValue
                    && ServerShopPricing.TotalPrice(int.MaxValue, 1, 0, 2) == int.MaxValue, "Flat/minimum price aggregation or saturation changed.");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
