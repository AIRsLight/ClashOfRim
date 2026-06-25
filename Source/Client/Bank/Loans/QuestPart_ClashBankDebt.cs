using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

public sealed class QuestPart_ClashBankDebt : QuestPartActivable
{
    public string DebtId = string.Empty;
    public int AmountSilver;
    public string SourceKind = string.Empty;
    public string Reason = string.Empty;
    public long DueAtGameTicks;
    public int PenaltyIntervalTicks;
    public int PenaltyRaidPoints;
    public int TriggeredPenaltyCount;
    public long NextPenaltyGameTicks;
    public bool Completed;
    public bool DueWarningSent;
    public List<BankOverduePenaltyStageState> OverduePenaltyStages = new();

    public override string DescriptionPart => Completed
        ? ClashOfRimText.Key("ClashOfRim.BankDebt.QuestDescriptionRepaid", AmountSilver.Named("TOTAL"))
        : ClashOfRimText.Key(
            "ClashOfRim.BankDebt.QuestDescription",
            AmountSilver.Named("TOTAL"),
            FormatReason().Named("REASON"))
        + "\n"
        + ClashOfRimText.Key("ClashOfRim.BankDebt.QuestDueLine", FormatDueStatus().Named("DUE"));

    public override AlertReport AlertReport => !Completed && IsOverdue ? AlertReport.Active : AlertReport.Inactive;

    public override string ExpiryInfoPart => Completed ? string.Empty : FormatDueStatus();

    public override string ExpiryInfoPartTip => Completed
        ? string.Empty
        : ClashOfRimText.Key("ClashOfRim.BankDebt.QuestDueLine", FormatDueStatus().Named("DUE"));

    public override string AlertLabel => ClashOfRimText.Key("ClashOfRim.BankDebt.AlertLabel");

    public override string AlertExplanation => ClashOfRimText.Key(
        "ClashOfRim.BankDebt.AlertExplanation",
        AmountSilver.Named("TOTAL"),
        FormatReason().Named("REASON"));

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
        Scribe_Values.Look(ref DebtId, "debtId", string.Empty);
        Scribe_Values.Look(ref AmountSilver, "amountSilver");
        Scribe_Values.Look(ref SourceKind, "sourceKind", string.Empty);
        Scribe_Values.Look(ref Reason, "reason", string.Empty);
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

    private string FormatReason()
    {
        return Reason switch
        {
            "Mercenary:Death" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryDeath"),
            "Mercenary:HostileBehavior" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryHostileBehavior"),
            "Mercenary:HarmfulSurgery" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenarySurgery"),
            "Mercenary:OvertimeService" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryOvertimeService"),
            "Mercenary:ShuttleDestroyed" => ClashOfRimText.Key("ClashOfRim.Bank.ReasonMercenaryShuttleDestroyed"),
            _ => string.IsNullOrWhiteSpace(Reason) ? ClashOfRimText.Key("ClashOfRim.NotSpecified") : Reason
        };
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
        parms.customLetterLabel = ClashOfRimText.Key("ClashOfRim.BankDebt.OverdueRaidLabel");
        parms.customLetterText = ClashOfRimText.Key(
            "ClashOfRim.BankDebt.OverdueRaidText",
            AmountSilver.Named("TOTAL"),
            FormatReason().Named("REASON"));
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
                "ClashOfRim.BankDebt.DueSoonLetterText",
                AmountSilver.Named("TOTAL"),
                ClashManagedQuestTimingUtility.FormatRemainingPeriod(remainingTicks).Named("TIME")),
            LetterDefOf.NegativeEvent);
    }

    private void ApplyPenaltyStages(int penaltyCount)
    {
        BankOverduePenaltyUtility.ApplyGlobalPenalty(penaltyCount, OverduePenaltyStages, DebtId);
    }
}
