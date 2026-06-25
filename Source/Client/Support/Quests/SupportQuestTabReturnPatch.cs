using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AIRsLight.ClashOfRim.Support;

[HarmonyPatch(typeof(MainTabWindow_Quests), "DoSelectTargets")]
internal static class SupportQuestTabReturnPatch
{
    private static readonly AccessTools.FieldRef<MainTabWindow_Quests, Quest?> SelectedQuestRef =
        AccessTools.FieldRefAccess<MainTabWindow_Quests, Quest?>("selected");

    public static void Postfix(MainTabWindow_Quests __instance, Rect innerRect, ref float curY)
    {
        QuestPart_ClashSupportPawn? part = SelectedQuestRef(__instance)?
            .PartsListForReading
            .OfType<QuestPart_ClashSupportPawn>()
            .FirstOrDefault(candidate => candidate.CanStartEarlyReturnNow);
        if (part is null)
        {
            return;
        }

        curY += 4f;
        Rect buttonRect = new(innerRect.x, curY, innerRect.width, 25f);
        if (Widgets.ButtonText(buttonRect, ClashOfRimText.Key("ClashOfRim.Support.EarlyReturn")))
        {
            if (part.TryStartEarlyReturnNow())
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
            else
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                Messages.Message(
                    ClashOfRimText.Key("ClashOfRim.Support.EarlyReturnUnavailable"),
                    MessageTypeDefOf.RejectInput,
                    historical: false);
            }
        }

        TooltipHandler.TipRegion(buttonRect, ClashOfRimText.Key("ClashOfRim.Support.EarlyReturnDesc"));
        curY += 25f;
    }
}
