using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AIRsLight.ClashOfRim.Mercenaries;

[HarmonyPatch(typeof(MainTabWindow_Quests), "DoSelectTargets")]
internal static class MercenaryQuestTabRecallPatch
{
    private static readonly AccessTools.FieldRef<MainTabWindow_Quests, Quest?> SelectedQuestRef =
        AccessTools.FieldRefAccess<MainTabWindow_Quests, Quest?>("selected");

    public static void Postfix(MainTabWindow_Quests __instance, Rect innerRect, ref float curY)
    {
        QuestPart_ClashMercenary? part = SelectedQuestRef(__instance)?
            .PartsListForReading
            .OfType<QuestPart_ClashMercenary>()
            .FirstOrDefault(candidate => candidate.CanStartRecallNow);
        if (part is null)
        {
            return;
        }

        curY += 4f;
        Rect buttonRect = new(innerRect.x, curY, innerRect.width, 25f);
        if (Widgets.ButtonText(buttonRect, ClashOfRimText.Key("ClashOfRim.Mercenary.EarlyRecall")))
        {
            if (part.TryStartRecallNow())
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
            else
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                Messages.Message(
                    ClashOfRimText.Key("ClashOfRim.Mercenary.EarlyRecallUnavailable"),
                    MessageTypeDefOf.RejectInput,
                    historical: false);
            }
        }

        TooltipHandler.TipRegion(buttonRect, ClashOfRimText.Key("ClashOfRim.Mercenary.EarlyRecallDesc"));
        curY += 25f;
    }
}
