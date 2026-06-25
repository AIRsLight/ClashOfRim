using System;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

public sealed class QuestPart_ClashSupportPawn : QuestPartActivable
{
    public string EventId = string.Empty;
    public string PawnLabel = string.Empty;
    public string OwnerUserId = string.Empty;
    public bool PermanentSupport;
    public long ExpiresAtGameTicks;
    public bool Completed;
    public bool FinishRequested;
    public string FinishReason = string.Empty;

    public bool CanStartEarlyReturnNow => !Completed && !PermanentSupport && !FinishRequested;

    public override string DescriptionPart => PermanentSupport
        ? ClashOfRimText.Key("ClashOfRim.Support.QuestDescriptionPermanent", PawnLabel.Named("PAWN"), OwnerUserId.Named("OWNER"))
        : ClashOfRimText.Key("ClashOfRim.Support.QuestDescriptionTemporary", PawnLabel.Named("PAWN"), OwnerUserId.Named("OWNER"));

    public override AlertReport AlertReport => !Completed && IsExpired ? AlertReport.Active : AlertReport.Inactive;

    public override string AlertLabel => ClashOfRimText.Key("ClashOfRim.Support.QuestExpiredAlertLabel");

    public override string AlertExplanation => ClashOfRimText.Key("ClashOfRim.Support.QuestExpiredAlertExplanation", PawnLabel.Named("PAWN"));

    private bool IsExpired => !PermanentSupport && ClashManagedQuestTimingUtility.IsExpired(ExpiresAtGameTicks);

    public override void QuestPartTick()
    {
        base.QuestPartTick();
        if (Completed || string.IsNullOrWhiteSpace(EventId))
        {
            return;
        }

        if (PermanentSupport)
        {
            Completed = true;
            ClashManagedQuestUtility.EndManagedQuest(this, QuestEndOutcome.Success, sendLetter: false, playSound: false);
            return;
        }

        ClashOfRimGameComponent? component = Verse.Current.Game?.GetComponent<ClashOfRimGameComponent>();
        bool assignmentStillExists = component?.HasSupportAssignment(EventId) == true;
        if (!assignmentStillExists)
        {
            Completed = true;
            ClashManagedQuestUtility.EndManagedQuest(this, QuestEndOutcome.Success, sendLetter: false, playSound: false);
            return;
        }

        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (FinishRequested)
        {
            _ = mod?.TryConfirmDepartedSupportAssignment(EventId, string.IsNullOrWhiteSpace(FinishReason) ? "QuestExpired" : FinishReason);
            return;
        }

        if (IsExpired)
        {
            if (mod?.TryStartSupportAssignmentReturnFromQuest(EventId, "QuestExpired") == true)
            {
                FinishRequested = true;
                FinishReason = "QuestExpired";
            }
        }
    }

    public bool TryStartEarlyReturnNow()
    {
        if (!CanStartEarlyReturnNow)
        {
            return false;
        }

        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod?.TryStartSupportAssignmentReturnFromQuest(EventId, "EarlyReturn") != true)
        {
            return false;
        }

        FinishRequested = true;
        FinishReason = "EarlyReturn";
        return true;
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref EventId, "eventId", string.Empty);
        Scribe_Values.Look(ref PawnLabel, "pawnLabel", string.Empty);
        Scribe_Values.Look(ref OwnerUserId, "ownerUserId", string.Empty);
        Scribe_Values.Look(ref PermanentSupport, "permanentSupport");
        Scribe_Values.Look(ref ExpiresAtGameTicks, "expiresAtGameTicks");
        Scribe_Values.Look(ref Completed, "completed");
        Scribe_Values.Look(ref FinishRequested, "finishRequested");
        Scribe_Values.Look(ref FinishReason, "finishReason", string.Empty);
    }
}
