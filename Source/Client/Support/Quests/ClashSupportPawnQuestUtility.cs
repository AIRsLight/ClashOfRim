using System;
using System.Linq;
using AIRsLight.ClashOfRim.Quests;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal static class ClashSupportPawnQuestUtility
{
    public static void CreateOrUpdateSupportQuest(ActiveSupportPawnAssignment assignment)
    {
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.EventId))
        {
            return;
        }

        QuestPart_ClashSupportPawn? existing = FindSupportPart(assignment.EventId);
        if (existing is not null)
        {
            existing.PawnLabel = assignment.PawnLabel;
            existing.OwnerUserId = assignment.OwnerUserId;
            existing.PermanentSupport = assignment.PermanentSupport;
            existing.ExpiresAtGameTicks = assignment.ExpiresAtGameTicks.GetValueOrDefault();
            return;
        }

        Quest quest = ClashManagedQuestUtility.CreateRawManagedQuest(
            ClashOfRimQuestDefOf.ClashOfRim_SupportPawn,
            ClashOfRimText.Key("ClashOfRim.Support.QuestName", assignment.PawnLabel.Named("PAWN")),
            assignment.PermanentSupport
                ? ClashOfRimText.Key("ClashOfRim.Support.QuestFullDescriptionPermanent", assignment.PawnLabel.Named("PAWN"), assignment.OwnerUserId.Named("OWNER"))
                : ClashOfRimText.Key("ClashOfRim.Support.QuestFullDescriptionTemporary", assignment.PawnLabel.Named("PAWN"), assignment.OwnerUserId.Named("OWNER")));
        var part = new QuestPart_ClashSupportPawn
        {
            EventId = assignment.EventId,
            PawnLabel = assignment.PawnLabel,
            OwnerUserId = assignment.OwnerUserId,
            PermanentSupport = assignment.PermanentSupport,
            ExpiresAtGameTicks = assignment.ExpiresAtGameTicks.GetValueOrDefault(),
            inSignalEnable = quest.InitiateSignal
        };
        quest.AddPart(part);
        ClashManagedQuestUtility.AddAcceptedManagedQuest(quest);
    }

    public static void CompleteSupportQuest(string eventId)
    {
        QuestPart_ClashSupportPawn? part = FindSupportPart(eventId);
        if (part is null || part.Completed)
        {
            return;
        }

        part.Completed = true;
        ClashManagedQuestUtility.EndManagedQuest(part, QuestEndOutcome.Success, sendLetter: false, playSound: false);
    }

    private static QuestPart_ClashSupportPawn? FindSupportPart(string eventId)
    {
        return Find.QuestManager?.QuestsListForReading?
            .SelectMany(quest => quest.PartsListForReading.OfType<QuestPart_ClashSupportPawn>())
            .FirstOrDefault(part => string.Equals(part.EventId, eventId, StringComparison.Ordinal));
    }
}
