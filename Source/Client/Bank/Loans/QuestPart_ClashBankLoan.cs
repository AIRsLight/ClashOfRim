using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

public sealed class QuestPart_ClashBankLoan : QuestPartActivable
{
    public string LoanId = string.Empty;
    public int PrincipalSilver;
    public int InterestSilver;
    public int LateFeeSilver;
    public int TotalDueSilver;
    public float AnnualInterestRate;
    public long DueAtGameTicks;
    public int PenaltyIntervalTicks;
    public int PenaltyRaidPoints;
    public int TriggeredPenaltyCount;
    public long NextPenaltyGameTicks;
    public bool Completed;
    public bool DueWarningSent;
    public List<BankOverduePenaltyStageState> OverduePenaltyStages = new();

    public override string DescriptionPart => Completed
        ? ClashOfRimText.Key("ClashOfRim.Bank.QuestDescriptionRepaid", TotalDueSilver.Named("TOTAL"))
        : FormatDescription();

    public override AlertReport AlertReport => !Completed && IsOverdue ? AlertReport.Active : AlertReport.Inactive;

    public override string ExpiryInfoPart => Completed ? string.Empty : FormatDueStatus();

    public override string ExpiryInfoPartTip => Completed
        ? string.Empty
        : ClashOfRimText.Key("ClashOfRim.Bank.QuestDueLine", FormatDueStatus().Named("DUE"));

    public override string AlertLabel => ClashOfRimText.Key("ClashOfRim.Bank.AlertLabel");

    public override string AlertExplanation => ClashOfRimText.Key(
        "ClashOfRim.Bank.AlertExplanation",
        TotalDueSilver.Named("TOTAL"));

    private bool IsOverdue => ClashManagedQuestTimingUtility.IsExpired(DueAtGameTicks);

    private string FormatDueStatus()
    {
        return ClashManagedQuestTimingUtility.FormatDueStatus(
            DueAtGameTicks,
            "ClashOfRim.Unknown",
            "ClashOfRim.Bank.DebtDueOverdue",
            "ClashOfRim.Bank.DebtDueIn");
    }

    public override void QuestPartTick()
    {
        base.QuestPartTick();
        if (Completed || DueAtGameTicks <= 0)
        {
            return;
        }

        long ticks = ClashManagedQuestTimingUtility.CurrentGameTicks;
        MaybeSendDueWarning();
        if (PenaltyIntervalTicks <= 0)
        {
            return;
        }

        if (ticks < DueAtGameTicks)
        {
            return;
        }

        if (NextPenaltyGameTicks <= 0)
        {
            NextPenaltyGameTicks = DueAtGameTicks;
        }

        if (ticks < NextPenaltyGameTicks)
        {
            return;
        }

        TriggeredPenaltyCount++;
        AddLateFee();
        TriggerMechRaid();
        ApplyPenaltyStages(TriggeredPenaltyCount);
        NextPenaltyGameTicks = ticks + PenaltyIntervalTicks;
    }

    public void MarkRepaid()
    {
        Completed = true;
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref LoanId, "loanId", string.Empty);
        Scribe_Values.Look(ref PrincipalSilver, "principalSilver");
        Scribe_Values.Look(ref InterestSilver, "interestSilver");
        Scribe_Values.Look(ref LateFeeSilver, "lateFeeSilver");
        Scribe_Values.Look(ref TotalDueSilver, "totalDueSilver");
        Scribe_Values.Look(ref AnnualInterestRate, "annualInterestRate");
        Scribe_Values.Look(ref DueAtGameTicks, "dueAtGameTicks");
        Scribe_Values.Look(ref PenaltyIntervalTicks, "penaltyIntervalTicks");
        Scribe_Values.Look(ref PenaltyRaidPoints, "penaltyRaidPoints");
        Scribe_Values.Look(ref TriggeredPenaltyCount, "triggeredPenaltyCount");
        Scribe_Values.Look(ref NextPenaltyGameTicks, "nextPenaltyGameTicks");
        Scribe_Values.Look(ref Completed, "completed");
        Scribe_Values.Look(ref DueWarningSent, "dueWarningSent");
        Scribe_Collections.Look(ref OverduePenaltyStages, "overduePenaltyStages", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.PostLoadInit && OverduePenaltyStages is null)
        {
            OverduePenaltyStages = new List<BankOverduePenaltyStageState>();
        }
    }

    private string FormatDescription()
    {
        if (LateFeeSilver <= 0)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.Bank.QuestDescription",
                PrincipalSilver.Named("PRINCIPAL"),
                InterestSilver.Named("INTEREST"),
                TotalDueSilver.Named("TOTAL"));
        }

        return ClashOfRimText.Key(
            "ClashOfRim.Bank.QuestDescriptionWithLateFee",
            PrincipalSilver.Named("PRINCIPAL"),
            InterestSilver.Named("INTEREST"),
            LateFeeSilver.Named("LATEFEE"),
            TotalDueSilver.Named("TOTAL"));
    }

    private void AddLateFee()
    {
        if (AnnualInterestRate <= 0f || PenaltyIntervalTicks <= 0)
        {
            return;
        }

        double intervalDays = Math.Max(1d, (double)PenaltyIntervalTicks / ClashManagedQuestTimingUtility.TicksPerDay);
        double fee = Math.Max(0, TotalDueSilver)
            * AnnualInterestRate
            * 2d
            * intervalDays
            / 60d;
        int lateFee = Math.Max(1, (int)Math.Ceiling(fee));
        LateFeeSilver += lateFee;
        TotalDueSilver += lateFee;
    }

    private void TriggerMechRaid()
    {
        Map? map = Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome)
            ?? Find.CurrentMap;
        if (map is null)
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.Bank.OverdueNoMap"),
                MessageTypeDefOf.ThreatSmall,
                historical: true);
            return;
        }

        IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
        parms.target = map;
        parms.points = Math.Max(1, PenaltyRaidPoints);
        parms.faction = Faction.OfMechanoids;
        parms.forced = true;
        parms.quest = quest;
        parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
        parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
        parms.customLetterLabel = ClashOfRimText.Key("ClashOfRim.Bank.OverdueRaidLabel");
        parms.customLetterText = ClashOfRimText.Key(
            "ClashOfRim.Bank.OverdueRaidText",
            TotalDueSilver.Named("TOTAL"));
        parms.sendLetter = true;
        if (!IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.Bank.OverdueRaidFailed"),
                MessageTypeDefOf.ThreatSmall,
                historical: true);
        }
    }

    private void MaybeSendDueWarning()
    {
        if (!ClashManagedQuestTimingUtility.ShouldSendDueWarning(
                DueAtGameTicks,
                DueWarningSent,
                ClashManagedQuestTimingUtility.TicksPerDay,
                out long remainingTicks))
        {
            return;
        }

        DueWarningSent = true;
        Find.LetterStack.ReceiveLetter(
            ClashOfRimText.Key("ClashOfRim.Bank.DueSoonLetterLabel"),
            ClashOfRimText.Key(
                "ClashOfRim.Bank.DueSoonLoanLetterText",
                TotalDueSilver.Named("TOTAL"),
                ClashManagedQuestTimingUtility.FormatRemainingPeriod(remainingTicks).Named("TIME")),
            LetterDefOf.NegativeEvent);
    }

    private void ApplyPenaltyStages(int penaltyCount)
    {
        BankOverduePenaltyUtility.ApplyGlobalPenalty(penaltyCount, OverduePenaltyStages, LoanId);
    }
}
