using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

public sealed class QuestPart_ClashMercenary : QuestPartActivable
{
    public string ContractId = string.Empty;
    public Pawn? Pawn;
    public string PawnLabel = string.Empty;
    public string SkillDefName = string.Empty;
    public int SkillLevel;
    public int PriceSilver;
    public int HarmfulSurgeryFineSilver;
    public int DeathFineSilver;
    public long ExpiresAtGameTicks;
    public string QuestTag = string.Empty;
    public bool Completed;
    public bool RecallStarted;
    public bool RecallShuttleWaiting;
    public Thing? RecallShuttle;
    public TransportShip? RecallShip;
    public bool RecallShuttleLoaded;
    public bool ArrivalLetterSent;
    public bool HostileBehaviorReported;
    public bool DeathReported;
    public bool RecallShuttleDestroyedReported;
    public int ReportedOvertimeFineDays;

    public override string ExpiryInfoPart
    {
        get
        {
            if (Completed || RecallStarted || ExpiresAtGameTicks <= 0)
            {
                return null!;
            }

            return ClashOfRimText.Key(
                "ClashOfRim.Mercenary.QuestLeavesIn",
                ClashManagedQuestTimingUtility.FormatRemainingPeriod(
                    ClashManagedQuestTimingUtility.RemainingTicks(ExpiresAtGameTicks)).Named("TIME"));
        }
    }

    public override string ExpiryInfoPartTip
    {
        get
        {
            if (Completed || RecallStarted || ExpiresAtGameTicks <= 0)
            {
                return null!;
            }

            int dueTick = (int)Math.Min(int.MaxValue, ExpiresAtGameTicks);
            return ClashOfRimText.Key(
                "ClashOfRim.Mercenary.QuestLeavesOn",
                GenDate.DateFullStringWithHourAt(
                    GenDate.TickGameToAbs(dueTick),
                    QuestUtility.GetLocForDates()).Named("DATE"));
        }
    }

    public override string DescriptionPart => Completed
        ? ClashOfRimText.Key("ClashOfRim.Mercenary.QuestDescriptionCompleted", PawnLabel.Named("PAWN"))
        : ClashOfRimText.Key(
            "ClashOfRim.Mercenary.QuestDescription",
            PawnLabel.Named("PAWN"),
            MercenarySkillUtility.ProfessionLabel(SkillDefName).Named("SKILL"),
            MercenarySkillUtility.TierLabel(SkillLevel).Named("TIER"));

    public override AlertReport AlertReport => !Completed && IsExpired ? AlertReport.Active : AlertReport.Inactive;

    public override string AlertLabel => ClashOfRimText.Key("ClashOfRim.Mercenary.AlertLabel");

    public override string AlertExplanation => ClashOfRimText.Key(
        "ClashOfRim.Mercenary.AlertExplanation",
        PawnLabel.Named("PAWN"));

    private bool IsExpired => ClashManagedQuestTimingUtility.IsExpired(ExpiresAtGameTicks);

    public bool CanStartRecallNow => !Completed && !RecallStarted && Pawn is not null && !Pawn.Destroyed;

    public override IEnumerable<GlobalTargetInfo> QuestLookTargets
    {
        get
        {
            if (Pawn is not null && !Pawn.Destroyed)
            {
                yield return Pawn;
            }
        }
    }

    public override string QuestSelectTargetsLabel => ClashOfRimText.Key("ClashOfRim.Mercenary.SelectPawn");

    public override IEnumerable<GlobalTargetInfo> QuestSelectTargets
    {
        get
        {
            if (!Completed && Pawn is not null && !Pawn.Destroyed)
            {
                yield return Pawn;
            }
        }
    }

    public override string ExtraInspectString(ISelectable target)
    {
        if (Completed || target != Pawn)
        {
            return null!;
        }

        if (RecallShuttleLoaded)
        {
            return ClashOfRimText.Key("ClashOfRim.Mercenary.InspectLoaded");
        }

        if (RecallShuttleWaiting)
        {
            return ClashOfRimText.Key("ClashOfRim.Mercenary.InspectWaitingForShuttle");
        }

        if (ExpiresAtGameTicks <= 0)
        {
            return ClashOfRimText.Key("ClashOfRim.Mercenary.InspectTemporary");
        }

        return ClashOfRimText.Key(
            "ClashOfRim.Mercenary.InspectLeavesIn",
            ClashManagedQuestTimingUtility.FormatRemainingPeriod(
                ClashManagedQuestTimingUtility.RemainingTicks(ExpiresAtGameTicks)).Named("TIME"));
    }

    public override void QuestPartTick()
    {
        base.QuestPartTick();
        if (Completed)
        {
            return;
        }

        if (RecallShuttleWaiting && TryCompleteRecallFromShuttleState())
        {
            return;
        }

        if (Pawn is null || Pawn.Destroyed)
        {
            Completed = true;
            ClashManagedQuestUtility.EndManagedQuest(this, QuestEndOutcome.Fail, sendLetter: true, playSound: true);
            LoadedModManager.GetMod<ClashOfRimMod>()?.StartMercenarySnapshotConfirmation();
            return;
        }

        TrySendArrivalLetter();

        if (!RecallStarted && IsExpired)
        {
            TryStartRecallNow();
        }

        if (RecallShuttleWaiting)
        {
            TryCompleteRecallFromShuttleState();
            ReportOvertimeFinesIfNeeded();
        }
    }

    public override void Notify_QuestSignalReceived(Signal signal)
    {
        base.Notify_QuestSignalReceived(signal);
        if (Completed || string.IsNullOrWhiteSpace(QuestTag))
        {
            return;
        }

        if (!HostileBehaviorReported && string.Equals(signal.tag, QuestTag + ".SurgeryViolation", StringComparison.Ordinal))
        {
            ReportHostileBehavior();
            return;
        }

        if (RecallShuttleWaiting && string.Equals(signal.tag, QuestTag + ".SentSatisfied", StringComparison.Ordinal))
        {
            CompleteRecallSuccess();
            return;
        }

        if (RecallShuttleWaiting && string.Equals(signal.tag, QuestTag + ".Destroyed", StringComparison.Ordinal))
        {
            ReportRecallShuttleDestroyed();
            return;
        }

        if (RecallShuttleWaiting && string.Equals(signal.tag, QuestTag + ".SentUnsatisfied", StringComparison.Ordinal))
        {
            ReportRecallShuttleDestroyed();
        }
    }

    private void ReportOvertimeFinesIfNeeded()
    {
        if (ExpiresAtGameTicks <= 0)
        {
            return;
        }

        long overdueTicks = ClashManagedQuestTimingUtility.CurrentGameTicks - ExpiresAtGameTicks;
        if (overdueTicks < ClashManagedQuestTimingUtility.TicksPerDay)
        {
            return;
        }

        int overdueDays = (int)Math.Min(
            int.MaxValue,
            overdueTicks / ClashManagedQuestTimingUtility.TicksPerDay);
        while (ReportedOvertimeFineDays < overdueDays)
        {
            ReportedOvertimeFineDays++;
            string idempotencyKey =
                $"mercenary-overtime:{ContractId}:{ReportedOvertimeFineDays}";
            LoadedModManager.GetMod<ClashOfRimMod>()?.StartReportMercenaryIncident(
                ContractId,
                "OvertimeService",
                idempotencyKey);
        }
    }

    private void TrySendArrivalLetter()
    {
        if (ArrivalLetterSent || Pawn is null || Pawn.Destroyed || !Pawn.Spawned)
        {
            return;
        }

        ArrivalLetterSent = true;
        Find.LetterStack.ReceiveLetter(
            ClashOfRimText.Key("ClashOfRim.Mercenary.ArrivalLetterLabel"),
            ClashOfRimText.Key("ClashOfRim.Mercenary.ArrivalLetterText", Pawn.LabelShort.Named("PAWN")),
            LetterDefOf.PositiveEvent,
            Pawn);
    }

    private bool TryCompleteRecallFromShuttleState()
    {
        if (!RecallShuttleWaiting)
        {
            return false;
        }

        if (IsPawnLoadedInRecallShuttle())
        {
            RecallShuttleLoaded = true;
        }

        if (RecallShuttleLoaded)
        {
            if (RecallShip?.Disposed == true || RecallShuttle is null || RecallShuttle.Destroyed || !RecallShuttle.Spawned)
            {
                CompleteRecallSuccess();
                return true;
            }
        }
        else if (RecallShuttle is not null && RecallShuttle.Destroyed)
        {
            ReportRecallShuttleDestroyed();
            return true;
        }

        return false;
    }

    public bool TryStartRecallNow()
    {
        Pawn? pawn = Pawn;
        if (Completed || RecallStarted || pawn is null || pawn.Destroyed)
        {
            return false;
        }

        RecallStarted = true;
        bool recalled = ClashMercenaryQuestUtility.TryRecallMercenary(
            pawn,
            QuestTag,
            out bool waitsForLoading,
            out Thing? recallShuttle,
            out TransportShip? recallShip);
        RecallShuttle = recallShuttle;
        RecallShip = recallShip;
        Find.LetterStack.ReceiveLetter(
            ClashOfRimText.Key("ClashOfRim.Mercenary.RecallLetterLabel"),
            ClashOfRimText.Key("ClashOfRim.Mercenary.RecallLetterText", PawnLabel.Named("PAWN")),
            LetterDefOf.NeutralEvent,
            RecallShuttle ?? pawn);
        RecallShuttleWaiting = recalled && waitsForLoading;
        if (recalled && !waitsForLoading)
        {
            Completed = true;
            ClashManagedQuestUtility.EndManagedQuest(this, QuestEndOutcome.Success, sendLetter: false, playSound: false);
            StartSuccessfulCompletionSnapshotConfirmation();
        }

        return recalled;
    }

    private bool IsPawnLoadedInRecallShuttle()
    {
        if (Pawn is null || RecallShuttle is null)
        {
            return false;
        }

        CompTransporter? transporter = RecallShuttle.TryGetComp<CompTransporter>();
        return transporter?.innerContainer?.Contains(Pawn) == true;
    }

    private void CompleteRecallSuccess()
    {
        if (Completed)
        {
            return;
        }

        RecallShuttleWaiting = false;
        RecallShuttle = null;
        RecallShip = null;
        Completed = true;
        ClashManagedQuestUtility.EndManagedQuest(this, QuestEndOutcome.Success, sendLetter: false, playSound: false);
        StartSuccessfulCompletionSnapshotConfirmation();
    }

    private void StartSuccessfulCompletionSnapshotConfirmation()
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ContractId))
        {
            mod.StartMercenarySnapshotConfirmation();
            return;
        }

        mod.StartMercenarySnapshotConfirmation(
            ContractId,
            $"mercenary-completed:{ContractId}");
    }

    public void ReportHarmfulSurgery()
    {
        ReportHostileBehavior();
    }

    public void ReportHostileBehavior()
    {
        if (Completed || HostileBehaviorReported)
        {
            return;
        }

        HostileBehaviorReported = true;
        LoadedModManager.GetMod<ClashOfRimMod>()?.StartReportMercenaryIncident(
            ContractId,
            "HostileBehavior");
        Find.LetterStack.ReceiveLetter(
            ClashOfRimText.Key("ClashOfRim.Mercenary.HostileBehaviorLetterLabel"),
            ClashOfRimText.Key(
                "ClashOfRim.Mercenary.HostileBehaviorLetterText",
                PawnLabel.Named("PAWN"),
                HarmfulSurgeryFineSilver.Named("FINE")),
            LetterDefOf.NegativeEvent,
            Pawn);
    }

    private void ReportRecallShuttleDestroyed()
    {
        if (Completed || RecallShuttleDestroyedReported)
        {
            return;
        }

        Thing? recallShuttle = RecallShuttle;
        RecallShuttleDestroyedReported = true;
        RecallShuttleWaiting = false;
        RecallShuttle = null;
        RecallShip = null;
        Completed = true;
        ClashMercenaryQuestUtility.ReleaseMercenaryAfterRecallShuttleDestroyed(Pawn);
        LoadedModManager.GetMod<ClashOfRimMod>()?.StartReportMercenaryIncident(
            ContractId,
            "ShuttleDestroyed",
            $"mercenary-shuttle-destroyed:{ContractId}");
        Find.LetterStack.ReceiveLetter(
            ClashOfRimText.Key("ClashOfRim.Mercenary.ShuttleDestroyedLetterLabel"),
            ClashOfRimText.Key(
                "ClashOfRim.Mercenary.ShuttleDestroyedLetterText",
                PawnLabel.Named("PAWN"),
                6000.Named("FINE")),
            LetterDefOf.NegativeEvent,
            recallShuttle ?? Pawn);
        ClashManagedQuestUtility.EndManagedQuest(this, QuestEndOutcome.Fail, sendLetter: false, playSound: true);
    }

    public override void Notify_PawnKilled(Pawn pawn, DamageInfo? dinfo)
    {
        base.Notify_PawnKilled(pawn, dinfo);
        if (Completed || DeathReported || pawn != Pawn)
        {
            return;
        }

        DeathReported = true;
        Completed = true;
        LoadedModManager.GetMod<ClashOfRimMod>()?.StartReportMercenaryIncident(
            ContractId,
            "Death");
        Find.LetterStack.ReceiveLetter(
            ClashOfRimText.Key("ClashOfRim.Mercenary.DeathLetterLabel"),
            ClashOfRimText.Key(
                "ClashOfRim.Mercenary.DeathLetterText",
                PawnLabel.Named("PAWN"),
                DeathFineSilver.Named("FINE")),
            LetterDefOf.NegativeEvent,
            pawn);
        ClashManagedQuestUtility.EndManagedQuest(this, QuestEndOutcome.Fail, sendLetter: false, playSound: true);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref ContractId, "contractId", string.Empty);
        Scribe_References.Look(ref Pawn, "pawn");
        Scribe_Values.Look(ref PawnLabel, "pawnLabel", string.Empty);
        Scribe_Values.Look(ref SkillDefName, "skillDefName", string.Empty);
        Scribe_Values.Look(ref SkillLevel, "skillLevel");
        Scribe_Values.Look(ref PriceSilver, "priceSilver");
        Scribe_Values.Look(ref HarmfulSurgeryFineSilver, "harmfulSurgeryFineSilver");
        Scribe_Values.Look(ref DeathFineSilver, "deathFineSilver");
        Scribe_Values.Look(ref ExpiresAtGameTicks, "expiresAtGameTicks");
        Scribe_Values.Look(ref QuestTag, "questTag", string.Empty);
        Scribe_Values.Look(ref Completed, "completed");
        Scribe_Values.Look(ref RecallStarted, "recallStarted");
        Scribe_Values.Look(ref RecallShuttleWaiting, "recallShuttleWaiting");
        Scribe_References.Look(ref RecallShuttle, "recallShuttle");
        Scribe_References.Look(ref RecallShip, "recallShip");
        Scribe_Values.Look(ref RecallShuttleLoaded, "recallShuttleLoaded");
        Scribe_Values.Look(ref ArrivalLetterSent, "arrivalLetterSent");
        Scribe_Values.Look(ref HostileBehaviorReported, "harmfulSurgeryReported");
        Scribe_Values.Look(ref DeathReported, "deathReported");
        Scribe_Values.Look(ref RecallShuttleDestroyedReported, "recallShuttleDestroyedReported");
        Scribe_Values.Look(ref ReportedOvertimeFineDays, "reportedOvertimeFineDays");
    }
}
