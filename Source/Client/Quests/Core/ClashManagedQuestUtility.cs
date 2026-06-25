using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Support;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Quests;

internal static class ClashManagedQuestUtility
{
    private static readonly HashSet<string> ManagedQuestRootDefNames = new(StringComparer.Ordinal)
    {
        "ClashOfRim_BankLoan",
        "ClashOfRim_BankDebt",
        "ClashOfRim_SupportPawn",
        "ClashOfRim_Mercenary"
    };

    public static bool IsProtectedFromManualDismiss(Quest? quest)
    {
        return IsManagedQuest(quest) && quest is { Historical: false };
    }

    public static bool IsManagedQuest(Quest? quest)
    {
        if (quest is null)
        {
            return false;
        }

        if (quest.root is not null)
        {
            if (quest.root == ClashOfRimQuestDefOf.ClashOfRim_BankLoan
                || quest.root == ClashOfRimQuestDefOf.ClashOfRim_BankDebt
                || quest.root == ClashOfRimQuestDefOf.ClashOfRim_SupportPawn
                || quest.root == ClashOfRimQuestDefOf.ClashOfRim_Mercenary
                || ManagedQuestRootDefNames.Contains(quest.root.defName))
            {
                return true;
            }
        }

        return quest.PartsListForReading.Any(part =>
            part is QuestPart_ClashBankLoan
                or QuestPart_ClashBankDebt
                or QuestPart_ClashSupportPawn
                or QuestPart_ClashMercenary);
    }

    public static void EnsureActiveManagedQuestVisible(Quest? quest)
    {
        if (!IsProtectedFromManualDismiss(quest))
        {
            return;
        }

        quest!.dismissed = false;
        quest.hiddenInUI = false;
    }

    public static Quest CreateRawManagedQuest(QuestScriptDef root, string name, string description)
    {
        Quest quest = Quest.MakeRaw();
        quest.root = root;
        quest.name = name ?? string.Empty;
        quest.description = description ?? string.Empty;
        return quest;
    }

    public static void AddAcceptedManagedQuest(Quest quest)
    {
        quest.SetInitiallyAccepted();
        Find.QuestManager.Add(quest);
        EnsureActiveManagedQuestVisible(quest);
    }

    public static void EndManagedQuest(
        QuestPart? part,
        QuestEndOutcome outcome,
        bool sendLetter,
        bool playSound)
    {
        part?.quest?.End(outcome, sendLetter, playSound);
    }
}
