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
        _ = ClientMercenaryQuestCallbacks.Current?.TickAsync(this);
    }

    public override void Notify_QuestSignalReceived(Signal signal)
    {
        base.Notify_QuestSignalReceived(signal);
        _ = ClientMercenaryQuestCallbacks.Current?.SignalAsync(this, signal.tag);
    }

    public bool TryStartRecallNow() => ClientMercenaryQuestCallbacks.Current?.TryStartRecall(this) == true;

    public void ReportHarmfulSurgery() => ReportHostileBehavior();

    public void ReportHostileBehavior() => _ = ClientMercenaryQuestCallbacks.Current?.HostileAsync(this);

    public override void Notify_PawnKilled(Pawn pawn, DamageInfo? dinfo)
    {
        base.Notify_PawnKilled(pawn, dinfo);
        _ = ClientMercenaryQuestCallbacks.Current?.KilledAsync(this, pawn);
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
