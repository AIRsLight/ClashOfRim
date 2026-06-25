using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

internal static class ClashBankLoanQuestUtility
{
    private const int FineDebtDueDays = 7;
    private static BankQuestPartCache? questPartCache;

    public static void CreateOrUpdateLoanQuest(ModBankLoanSummaryDto loan, ModBankStatusResponseDto status)
    {
        if (loan is null || string.IsNullOrWhiteSpace(loan.LoanId))
        {
            return;
        }

        QuestPart_ClashBankLoan? existing = FindLoanPart(loan.LoanId);
        if (existing is not null)
        {
            existing.PrincipalSilver = loan.PrincipalSilver;
            existing.InterestSilver = loan.InterestSilver;
            existing.TotalDueSilver = Math.Max(loan.TotalDueSilver + existing.LateFeeSilver, existing.TotalDueSilver);
            existing.AnnualInterestRate = Math.Max(0f, status.BaseAnnualInterestRate);
            existing.DueAtGameTicks = loan.DueAtGameTicks;
            existing.PenaltyRaidPoints = loan.PenaltyRaidPoints;
            existing.OverduePenaltyStages = ToStageStates(status.OverduePenaltyStages);
            return;
        }

        Quest quest = ClashManagedQuestUtility.CreateRawManagedQuest(
            ClashOfRimQuestDefOf.ClashOfRim_BankLoan,
            ClashOfRimText.Key("ClashOfRim.Bank.QuestName", loan.TotalDueSilver.Named("TOTAL")),
            ClashOfRimText.Key(
                "ClashOfRim.Bank.QuestFullDescription",
                loan.PrincipalSilver.Named("PRINCIPAL"),
                loan.InterestSilver.Named("INTEREST"),
                loan.TotalDueSilver.Named("TOTAL"),
                loan.DurationDays.Named("DAYS")));
        quest.points = loan.PenaltyRaidPoints;

        var part = new QuestPart_ClashBankLoan
        {
            LoanId = loan.LoanId,
            PrincipalSilver = loan.PrincipalSilver,
            InterestSilver = loan.InterestSilver,
            TotalDueSilver = loan.TotalDueSilver,
            AnnualInterestRate = Math.Max(0f, status.BaseAnnualInterestRate),
            DueAtGameTicks = loan.DueAtGameTicks,
            PenaltyIntervalTicks = Math.Max(1, status.PenaltyIntervalDays) * ClashManagedQuestTimingUtility.TicksPerDay,
            PenaltyRaidPoints = loan.PenaltyRaidPoints,
            OverduePenaltyStages = ToStageStates(status.OverduePenaltyStages),
            inSignalEnable = quest.InitiateSignal
        };
        quest.AddPart(part);
        ClashManagedQuestUtility.AddAcceptedManagedQuest(quest);
        InvalidateQuestPartCache();
    }

    public static void CreateOrUpdateDebtQuest(ModBankDebtSummaryDto debt, ModBankStatusResponseDto status)
    {
        if (debt is null || string.IsNullOrWhiteSpace(debt.DebtId))
        {
            return;
        }

        if (!IsActiveDebtStatus(debt.Status))
        {
            MarkDebtRepaid(debt.DebtId);
            return;
        }

        QuestPart_ClashBankDebt? existing = FindDebtPart(debt.DebtId);
        if (existing is not null)
        {
            existing.AmountSilver = debt.AmountSilver;
            existing.SourceKind = debt.SourceKind;
            existing.Reason = debt.Reason;
            existing.PenaltyRaidPoints = CalculateDebtPenaltyRaidPoints(debt, status);
            existing.OverduePenaltyStages = ToStageStates(status.OverduePenaltyStages);
            return;
        }

        long dueAtGameTicks = ClashManagedQuestTimingUtility.CurrentGameTicks + FineDebtDueDays * ClashManagedQuestTimingUtility.TicksPerDay;
        Quest quest = ClashManagedQuestUtility.CreateRawManagedQuest(
            ClashOfRimQuestDefOf.ClashOfRim_BankDebt,
            ClashOfRimText.Key("ClashOfRim.BankDebt.QuestName", debt.AmountSilver.Named("TOTAL")),
            ClashOfRimText.Key(
                "ClashOfRim.BankDebt.QuestFullDescription",
                debt.AmountSilver.Named("TOTAL"),
                FormatDebtReason(debt.Reason).Named("REASON"),
                FineDebtDueDays.Named("DAYS")));
        quest.points = CalculateDebtPenaltyRaidPoints(debt, status);

        var part = new QuestPart_ClashBankDebt
        {
            DebtId = debt.DebtId,
            AmountSilver = debt.AmountSilver,
            SourceKind = debt.SourceKind,
            Reason = debt.Reason,
            DueAtGameTicks = dueAtGameTicks,
            PenaltyIntervalTicks = Math.Max(1, status.PenaltyIntervalDays) * ClashManagedQuestTimingUtility.TicksPerDay,
            PenaltyRaidPoints = CalculateDebtPenaltyRaidPoints(debt, status),
            OverduePenaltyStages = ToStageStates(status.OverduePenaltyStages),
            inSignalEnable = quest.InitiateSignal
        };
        quest.AddPart(part);
        ClashManagedQuestUtility.AddAcceptedManagedQuest(quest);
        InvalidateQuestPartCache();
    }

    public static void CreateOrUpdateDebtQuests(ModBankStatusResponseDto? status)
    {
        if (status is null)
        {
            return;
        }

        foreach (ModBankDebtSummaryDto debt in status.OpenDebts)
        {
            CreateOrUpdateDebtQuest(debt, status);
        }
    }

    public static void MarkLoanRepaid(string? loanId)
    {
        if (string.IsNullOrWhiteSpace(loanId))
        {
            return;
        }

        QuestPart_ClashBankLoan? part = FindLoanPart(loanId!);
        if (part is null)
        {
            return;
        }

        part.MarkRepaid();
        ClashManagedQuestUtility.EndManagedQuest(part, QuestEndOutcome.Success, sendLetter: false, playSound: false);
        InvalidateQuestPartCache();
    }

    public static void MarkDebtRepaid(string? debtId)
    {
        if (string.IsNullOrWhiteSpace(debtId))
        {
            return;
        }

        QuestPart_ClashBankDebt? part = FindDebtPart(debtId!);
        if (part is null)
        {
            return;
        }

        part.MarkRepaid();
        ClashManagedQuestUtility.EndManagedQuest(part, QuestEndOutcome.Success, sendLetter: false, playSound: false);
        InvalidateQuestPartCache();
    }

    public static long? FindDebtDueAtGameTicks(string? debtId)
    {
        if (string.IsNullOrWhiteSpace(debtId))
        {
            return null;
        }

        return FindDebtPart(debtId!)?.DueAtGameTicks;
    }

    public static long? FindLoanDueAtGameTicks(string? loanId)
    {
        if (string.IsNullOrWhiteSpace(loanId))
        {
            return null;
        }

        return FindLoanPart(loanId!)?.DueAtGameTicks;
    }

    public static int? FindLoanTotalDueSilver(string? loanId)
    {
        if (string.IsNullOrWhiteSpace(loanId))
        {
            return null;
        }

        return FindLoanPart(loanId!)?.TotalDueSilver;
    }

    private static QuestPart_ClashBankLoan? FindLoanPart(string loanId)
    {
        BankQuestPartCache cache = GetQuestPartCache();
        return cache.LoansById.TryGetValue(loanId, out QuestPart_ClashBankLoan? part) ? part : null;
    }

    private static QuestPart_ClashBankDebt? FindDebtPart(string debtId)
    {
        BankQuestPartCache cache = GetQuestPartCache();
        return cache.DebtsById.TryGetValue(debtId, out QuestPart_ClashBankDebt? part) ? part : null;
    }

    private static BankQuestPartCache GetQuestPartCache()
    {
        QuestManager? questManager = Find.QuestManager;
        if (questPartCache is not null && ReferenceEquals(questPartCache.QuestManager, questManager))
        {
            return questPartCache;
        }

        var cache = new BankQuestPartCache(questManager);
        if (questManager?.QuestsListForReading is not null)
        {
            foreach (Quest quest in questManager.QuestsListForReading)
            {
                foreach (QuestPart part in quest.PartsListForReading)
                {
                    if (part is QuestPart_ClashBankLoan loanPart && !string.IsNullOrWhiteSpace(loanPart.LoanId))
                    {
                        cache.LoansById[loanPart.LoanId] = loanPart;
                    }
                    else if (part is QuestPart_ClashBankDebt debtPart && !string.IsNullOrWhiteSpace(debtPart.DebtId))
                    {
                        cache.DebtsById[debtPart.DebtId] = debtPart;
                    }
                }
            }
        }

        questPartCache = cache;
        return cache;
    }

    private static void InvalidateQuestPartCache()
    {
        questPartCache = null;
    }

    private static bool IsActiveDebtStatus(string? status)
    {
        return string.Equals(status, "Active", StringComparison.Ordinal);
    }

    private static List<BankOverduePenaltyStageState> ToStageStates(IEnumerable<ModBankOverduePenaltyStageDto>? stages)
    {
        return stages?
            .Select(stage => new BankOverduePenaltyStageState
            {
                TriggerPenaltyCount = stage.TriggerPenaltyCount,
                Kind = stage.Kind ?? string.Empty,
                Severity = stage.Severity
            })
            .ToList()
            ?? new List<BankOverduePenaltyStageState>();
    }

    private static int CalculateDebtPenaltyRaidPoints(ModBankDebtSummaryDto debt, ModBankStatusResponseDto status)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0, debt.AmountSilver) * status.PenaltyRaidPointsPerSilver));
    }

    private static string FormatDebtReason(string? reason)
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

    private sealed class BankQuestPartCache
    {
        public BankQuestPartCache(QuestManager? questManager)
        {
            QuestManager = questManager;
        }

        public QuestManager? QuestManager { get; }

        public Dictionary<string, QuestPart_ClashBankLoan> LoansById { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, QuestPart_ClashBankDebt> DebtsById { get; } = new(StringComparer.Ordinal);
    }
}
