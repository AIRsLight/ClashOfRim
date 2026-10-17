using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Bank;

internal static class BankLoanTerms
{
    internal static BankLoanSummaryDto ToBankLoanSummary(
        BankLoanRecord loan,
        ClashOfRimServerConfiguration configuration)
    {
        return new BankLoanSummaryDto(
            loan.LoanId,
            loan.PrincipalSilver,
            loan.InterestSilver,
            loan.TotalDueSilver,
            loan.DurationDays,
            loan.CreatedAtGameTicks,
            loan.DueAtGameTicks,
            CalculateBankPenaltyRaidPoints(loan.TotalDueSilver, configuration),
            loan.Status);
    }

    internal static BankDebtSummaryDto ToBankDebtSummary(BankDebtRecord debt)
    {
        return new BankDebtSummaryDto(
            debt.DebtId,
            debt.AmountSilver,
            debt.SourceKind,
            debt.Reason,
            debt.SourceId,
            debt.CreatedAtGameTicks,
            debt.Status);
    }

    internal static int CalculateBankMaxLoanSilver(int colonyWealth, ClashOfRimServerConfiguration configuration)
    {
        int wealthBasedLimit = (int)Math.Floor(Math.Max(0, colonyWealth) * configuration.BankMaxLoanWealthRatio);
        return configuration.BankMaxLoanSilver > 0
            ? Math.Min(wealthBasedLimit, configuration.BankMaxLoanSilver)
            : wealthBasedLimit;
    }

    internal static int CalculateBankInterestSilver(
        int principalSilver,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        double interest = Math.Max(0, principalSilver)
            * configuration.BankBaseAnnualInterestRate
            * Math.Max(0, durationDays)
            / BankLoanPolicy.RimWorldYearDays
            * InterpolateBankInterestDurationMultiplier(durationDays, configuration.BankInterestDurationMultiplierCurve);
        return (int)Math.Ceiling(interest);
    }

    internal static double InterpolateBankInterestDurationMultiplier(
        int durationDays,
        IReadOnlyList<BankInterestDurationMultiplierPointDto>? curve)
    {
        if (curve is null || curve.Count == 0)
        {
            return 1d;
        }

        List<BankInterestDurationMultiplierPointDto> points = curve
            .Where(point => point.DurationDays >= 0 && point.Multiplier >= 0f && !float.IsNaN(point.Multiplier))
            .OrderBy(point => point.DurationDays)
            .ToList();
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
            BankInterestDurationMultiplierPointDto previous = points[i - 1];
            BankInterestDurationMultiplierPointDto current = points[i];
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

        return points[^1].Multiplier;
    }

    internal static int CalculateBankPenaltyRaidPoints(int totalDueSilver, ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalDueSilver) * configuration.BankPenaltyRaidPointsPerSilver));
    }
}
