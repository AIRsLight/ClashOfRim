using AIRsLight.ClashOfRim.Modules.Bank;
using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Quests;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

internal static class BankUiUtility
{

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
    public static int CalculateInterestSilver(int principalSilver, int durationDays, ModBankStatusResponseDto status) =>
        ClientBankTerms.CalculateInterestSilver(principalSilver, durationDays, status);
}
