using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AIRsLight.ClashOfRim.Quests;

[HarmonyPatch(typeof(MainTabWindow_Quests), "DoDismissButton")]
internal static class ClashManagedQuestDismissButtonPatch
{
    private static readonly AccessTools.FieldRef<MainTabWindow_Quests, Quest?> SelectedQuestRef =
        AccessTools.FieldRefAccess<MainTabWindow_Quests, Quest?>("selected");

    public static bool Prefix(MainTabWindow_Quests __instance, Rect innerRect)
    {
        Quest? quest = SelectedQuestRef(__instance);
        if (!ClashManagedQuestUtility.IsProtectedFromManualDismiss(quest))
        {
            return true;
        }

        Rect rect = new(innerRect.xMax - 32f - 4f, innerRect.y, 32f, 32f);
        GUI.color = Color.gray;
        GUI.DrawTexture(rect, TexButton.Delete);
        GUI.color = Color.white;
        TooltipHandler.TipRegion(rect, ClashOfRimText.Key("ClashOfRim.Quest.ManagedCannotDismiss"));
        if (Widgets.ButtonInvisible(rect, doMouseoverSound: true))
        {
            SoundDefOf.ClickReject.PlayOneShotOnCamera();
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.Quest.ManagedCannotDismiss"),
                MessageTypeDefOf.RejectInput,
                historical: false);
        }

        ClashManagedQuestUtility.EnsureActiveManagedQuestVisible(quest);
        return false;
    }
}

[HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Add))]
internal static class ClashManagedQuestAddPatch
{
    public static void Prefix(Quest quest)
    {
        ClashManagedQuestUtility.EnsureActiveManagedQuestVisible(quest);
    }
}

[HarmonyPatch(typeof(QuestManager), nameof(QuestManager.ExposeData))]
internal static class ClashManagedQuestPostLoadPatch
{
    public static void Postfix(QuestManager __instance)
    {
        if (Scribe.mode != LoadSaveMode.PostLoadInit)
        {
            return;
        }

        foreach (Quest quest in __instance.QuestsListForReading)
        {
            ClashManagedQuestUtility.EnsureActiveManagedQuestVisible(quest);
        }
    }
}
