using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Quests;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

internal static class BankUiUtility
{
    private static IReadOnlyList<ModBankInterestDurationMultiplierPointDto>? cachedInterestCurveSource;
    private static List<ModBankInterestDurationMultiplierPointDto> cachedInterestCurvePoints = new();

    public static string FormatStatus(ModBankStatusResponseDto? status)
    {
        if (status is null)
        {
            return ClashOfRimText.Key("ClashOfRim.Bank.StatusMissing");
        }

        if (status.ActiveLoan is null)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.Bank.StatusReady",
                status.ColonyWealth.Named("WEALTH"),
                status.MinLoanSilver.Named("MIN"),
                status.MaxLoanSilver.Named("MAX"),
                FormatAnnualRate(status.BaseAnnualInterestRate).Named("RATE"));
        }

        return ClashOfRimText.Key(
            "ClashOfRim.Bank.StatusActiveLoan",
            status.ActiveLoan.TotalDueSilver.Named("TOTAL"),
            FormatLoanStatus(status.ActiveLoan.Status).Named("STATUS"));
    }

    public static string FormatLoanStatus(string? status)
    {
        return status switch
        {
            "PendingActivation" => ClashOfRimText.Key("ClashOfRim.Bank.StatusPendingActivation"),
            "Active" => ClashOfRimText.Key("ClashOfRim.Bank.StatusActive"),
            "PendingRepayment" => ClashOfRimText.Key("ClashOfRim.Bank.StatusPendingRepayment"),
            "Repaid" => ClashOfRimText.Key("ClashOfRim.Bank.StatusRepaidShort"),
            _ => string.IsNullOrWhiteSpace(status) ? ClashOfRimText.Key("ClashOfRim.Unknown") : status!
        };
    }

    public static string FormatDebtStatus(string? status)
    {
        return status switch
        {
            "PendingActivation" => ClashOfRimText.Key("ClashOfRim.Bank.StatusPendingActivation"),
            "Active" => ClashOfRimText.Key("ClashOfRim.Bank.StatusActive"),
            "PendingRepayment" => ClashOfRimText.Key("ClashOfRim.Bank.StatusPendingRepayment"),
            "Repaid" => ClashOfRimText.Key("ClashOfRim.Bank.StatusRepaidShort"),
            _ => string.IsNullOrWhiteSpace(status) ? ClashOfRimText.Key("ClashOfRim.Unknown") : status!
        };
    }

    public static string FormatDebtSource(string? sourceKind)
    {
        return sourceKind switch
        {
            "Loan" => ClashOfRimText.Key("ClashOfRim.Bank.SourceLoan"),
            "Fine" => ClashOfRimText.Key("ClashOfRim.Bank.SourceFine"),
            _ => string.IsNullOrWhiteSpace(sourceKind) ? ClashOfRimText.Key("ClashOfRim.Unknown") : sourceKind!
        };
    }

    public static string FormatDebtReason(string? reason)
    {
        return reason switch
        {
            "Mercenary:Death" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryDeath"),
            "Mercenary:HostileBehavior" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryHostileBehavior"),
            "Mercenary:HarmfulSurgery" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenarySurgery"),
            "Mercenary:OvertimeService" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryOvertimeService"),
            "Mercenary:ShuttleDestroyed" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryShuttleDestroyed"),
            _ => string.IsNullOrWhiteSpace(reason) ? ClashOfRimText.Key("ClashOfRim.NotSpecified") : reason!
        };
    }

    public static string FormatDebtDue(ModBankDebtSummaryDto debt)
    {
        if (string.Equals(debt.Status, "PendingRepayment", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.Bank.DebtDuePendingRepayment");
        }

        long? dueAt = ClashBankLoanQuestUtility.FindDebtDueAtGameTicks(debt.DebtId);
        if (!dueAt.HasValue || dueAt.Value <= 0)
        {
            return ClashOfRimText.Key("ClashOfRim.Bank.DebtDueQuestMissing");
        }

        return FormatDueAtGameTicks(dueAt.Value);
    }

    public static string FormatLoanDue(ModBankLoanSummaryDto loan)
    {
        if (string.Equals(loan.Status, "PendingRepayment", StringComparison.Ordinal))
        {
            return ClashOfRimText.Key("ClashOfRim.Bank.DebtDuePendingRepayment");
        }

        long? dueAt = ClashBankLoanQuestUtility.FindLoanDueAtGameTicks(loan.LoanId);
        if (!dueAt.HasValue || dueAt.Value <= 0)
        {
            return ClashOfRimText.Key("ClashOfRim.Bank.DebtDueQuestMissing");
        }

        return FormatDueAtGameTicks(dueAt.Value);
    }

    public static string FormatAnnualRate(float annualRate)
    {
        return (annualRate * 100f).ToString("0.#");
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
        IReadOnlyList<ModBankInterestDurationMultiplierPointDto> curve)
    {
        if (ReferenceEquals(curve, cachedInterestCurveSource))
        {
            return cachedInterestCurvePoints;
        }

        cachedInterestCurveSource = curve;
        cachedInterestCurvePoints = curve
            .Where(point => point.DurationDays >= 0 && point.Multiplier >= 0f && !float.IsNaN(point.Multiplier))
            .OrderBy(point => point.DurationDays)
            .ToList();
        return cachedInterestCurvePoints;
    }

    private static string FormatDueAtGameTicks(long dueAt)
    {
        long currentTicks = Find.TickManager?.TicksGame ?? 0;
        long remainingTicks = dueAt - currentTicks;
        if (remainingTicks <= 0)
        {
            return ClashOfRimText.Key("ClashOfRim.Bank.DebtDueOverdue");
        }

        return ClashOfRimText.Key(
            "ClashOfRim.Bank.DebtDueIn",
            ClashManagedQuestTimingUtility.FormatRemainingPeriod(remainingTicks).Named("TIME"));
    }
}
