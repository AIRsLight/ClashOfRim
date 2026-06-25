using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

[HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.CompGetGizmosExtra))]
public static class MercenaryRecallShuttleLoadGizmoPatch
{
    private static Texture2D? loadTransporterIcon;

    public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, CompShuttle __instance)
    {
        foreach (Gizmo gizmo in __result)
        {
            yield return gizmo;
        }

        if (!TryGetRecallTransporter(
                __instance,
                out CompTransporter? transporter,
                out QuestPart_ClashMercenary? mercenaryPart))
        {
            yield break;
        }

        CompTransporter recallTransporter = transporter!;
        yield return new Command_Action
        {
            defaultLabel = ClashOfRimText.Key("ClashOfRim.Mercenary.LoadRecallShuttle"),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.Mercenary.LoadRecallShuttleDesc"),
            icon = LoadTransporterIcon,
            action = () =>
            {
                if (mercenaryPart is null)
                {
                    return;
                }

                ClashMercenaryQuestUtility.TryStartRecallShuttleLoading(
                    mercenaryPart,
                    recallTransporter,
                    out string message,
                    out MessageTypeDef messageType);
                Messages.Message(
                    message,
                    __instance.parent,
                    messageType,
                    historical: false);
            }
        };
    }

    private static bool TryGetRecallTransporter(
        CompShuttle shuttle,
        out CompTransporter? transporter,
        out QuestPart_ClashMercenary? mercenaryPart)
    {
        transporter = null;
        mercenaryPart = null;
        ThingWithComps? parent = shuttle.parent;
        if (parent is null || parent.Destroyed || !parent.Spawned)
        {
            return false;
        }

        mercenaryPart = FindActiveMercenaryRecallPart(parent);
        if (mercenaryPart is null)
        {
            return false;
        }

        transporter = parent.TryGetComp<CompTransporter>();
        if (transporter is null || transporter.LoadingInProgressOrReadyToLaunch)
        {
            return false;
        }

        return true;
    }

    private static QuestPart_ClashMercenary? FindActiveMercenaryRecallPart(ThingWithComps shuttle)
    {
        if (shuttle.questTags is null || shuttle.questTags.Count == 0 || Find.QuestManager is null)
        {
            return null;
        }

        List<Quest> quests = Find.QuestManager.QuestsListForReading;
        for (int questIndex = 0; questIndex < quests.Count; questIndex++)
        {
            Quest quest = quests[questIndex];
            if (quest.State != QuestState.Ongoing)
            {
                continue;
            }

            List<QuestPart> parts = quest.PartsListForReading;
            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                if (parts[partIndex] is QuestPart_ClashMercenary part
                    && !part.Completed
                    && part.RecallShuttleWaiting
                    && !string.IsNullOrWhiteSpace(part.QuestTag)
                    && shuttle.questTags.Contains(part.QuestTag))
                {
                    return part;
                }
            }
        }

        return null;
    }

    private static Texture2D LoadTransporterIcon =>
        loadTransporterIcon ??= ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", reportFailure: false) ?? BaseContent.BadTex;
}
