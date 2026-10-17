using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Bank;

internal static class ClientBankTerms
{
    internal static int Maximum(int wealth, ModBankStatusResponseDto status)
    {
        int maximum = Math.Max(0, (int)Math.Floor(Math.Max(0, wealth) * status.MaxLoanWealthRatio));
        return status.ConfiguredMaxLoanSilver > 0 ? Math.Min(maximum, status.ConfiguredMaxLoanSilver) : maximum;
    }
    public static int CalculateInterestSilver(
        int principalSilver,
        int durationDays,
        ModBankStatusResponseDto status)
    {
        double interest = Math.Max(0, principalSilver)
            * Math.Max(0f, status.BaseAnnualInterestRate)
            * Math.Max(0, durationDays)
            / 60d
            * InterpolateInterestDurationMultiplier(durationDays, status.InterestDurationMultiplierCurve);
        return (int)Math.Ceiling(interest);
    }

    private static double InterpolateInterestDurationMultiplier(
        int durationDays,
        IReadOnlyList<ModBankInterestDurationMultiplierPointDto>? curve)
    {
        if (curve is null || curve.Count == 0)
        {
            return 1d;
        }

        List<ModBankInterestDurationMultiplierPointDto> points = NormalizedInterestCurvePoints(curve);
        if (points.Count == 0)
        {
            return 1d;
        }

        if (durationDays <= points[0].DurationDays)
        {
            return points[0].Multiplier;
        }

        for (int i = 1; i < points.Count; i++)
        {
            ModBankInterestDurationMultiplierPointDto previous = points[i - 1];
            ModBankInterestDurationMultiplierPointDto current = points[i];
            if (durationDays > current.DurationDays)
            {
                continue;
            }

            int span = current.DurationDays - previous.DurationDays;
            if (span <= 0)
            {
                return current.Multiplier;
            }

            double t = (double)(durationDays - previous.DurationDays) / span;
            return previous.Multiplier + (current.Multiplier - previous.Multiplier) * t;
        }

        return points[points.Count - 1].Multiplier;
    }

    private static List<ModBankInterestDurationMultiplierPointDto> NormalizedInterestCurvePoints(
        IReadOnlyList<ModBankInterestDurationMultiplierPointDto> curve) => curve
            .Where(point => point.DurationDays >= 0 && point.Multiplier >= 0f && !float.IsNaN(point.Multiplier))
            .OrderBy(point => point.DurationDays).ToList();
}
