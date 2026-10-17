using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Bank;

internal static class ClientBankView
{
    internal static ModBankStatusResponseDto Pending(ModBankStatusResponseDto source, ClientBankCommand command)
    {
        var result = Copy(source);
        switch (command.Operation)
        {
            case ClientBankOperation.Borrow: result.ActiveLoan = Copy(command.Loan!); break;
            case ClientBankOperation.RepayLoan: result.ActiveLoan = null; break;
            case ClientBankOperation.RepayDebt:
                result.OpenDebts.RemoveAll(d => d.DebtId == command.Debt!.DebtId);
                result.TotalOpenDebtSilver = result.OpenDebts.Sum(d => Math.Max(0, d.AmountSilver));
                break;
        }
        return result;
    }
    internal static ModBankStatusResponseDto WithLoan(ModBankStatusResponseDto source, ModBankLoanSummaryDto loan)
    { var result = Copy(source); result.ActiveLoan = Copy(loan); return result; }
    internal static ModBankStatusResponseDto Copy(ModBankStatusResponseDto source) => new()
    {
        Result = source.Result is null ? null : new() { Accepted = source.Result.Accepted, ErrorCode = source.Result.ErrorCode, Message = source.Result.Message },
        ColonyWealth = source.ColonyWealth,
        MinLoanSilver = source.MinLoanSilver,
        ConfiguredMaxLoanSilver = source.ConfiguredMaxLoanSilver,
        MaxLoanSilver = source.MaxLoanSilver,
        MaxLoanWealthRatio = source.MaxLoanWealthRatio,
        BaseAnnualInterestRate = source.BaseAnnualInterestRate,
        MinDurationDays = source.MinDurationDays,
        MaxDurationDays = source.MaxDurationDays,
        BankLoansEnabled = source.BankLoansEnabled,
        MercenariesEnabled = source.MercenariesEnabled,
        MercenaryMinDurationDays = source.MercenaryMinDurationDays,
        MercenaryMaxDurationDays = source.MercenaryMaxDurationDays,
        InterestDurationMultiplierCurve = source.InterestDurationMultiplierCurve?.Select(Copy).ToList() ?? new(),
        PenaltyIntervalDays = source.PenaltyIntervalDays,
        PenaltyRaidPointsPerSilver = source.PenaltyRaidPointsPerSilver,
        OverduePenaltyStages = source.OverduePenaltyStages?.Select(Copy).ToList() ?? new(),
        ActiveLoan = source.ActiveLoan is null ? null : Copy(source.ActiveLoan),
        OpenDebts = source.OpenDebts?.Select(Copy).ToList() ?? new(),
        TotalOpenDebtSilver = source.TotalOpenDebtSilver,
    };

    internal static ModBankLoanSummaryDto Copy(ModBankLoanSummaryDto source) => new()
    {
        LoanId = source.LoanId,
        PrincipalSilver = source.PrincipalSilver,
        InterestSilver = source.InterestSilver,
        TotalDueSilver = source.TotalDueSilver,
        DurationDays = source.DurationDays,
        CreatedAtGameTicks = source.CreatedAtGameTicks,
        DueAtGameTicks = source.DueAtGameTicks,
        PenaltyRaidPoints = source.PenaltyRaidPoints,
        Status = source.Status,
        SourceKind = source.SourceKind,
    };

    internal static ModBankDebtSummaryDto Copy(ModBankDebtSummaryDto source) => new()
    {
        DebtId = source.DebtId,
        AmountSilver = source.AmountSilver,
        SourceKind = source.SourceKind,
        Reason = source.Reason,
        SourceId = source.SourceId,
        CreatedAtGameTicks = source.CreatedAtGameTicks,
        Status = source.Status,
    };

    internal static ModBankInterestDurationMultiplierPointDto Copy(ModBankInterestDurationMultiplierPointDto source) => new()
    {
        DurationDays = source.DurationDays,
        Multiplier = source.Multiplier,
    };

    internal static ModBankOverduePenaltyStageDto Copy(ModBankOverduePenaltyStageDto source) => new()
    {
        TriggerPenaltyCount = source.TriggerPenaltyCount,
        Kind = source.Kind,
        Severity = source.Severity,
    };
}
