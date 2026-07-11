using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

internal static class CoreThingTransferCompatibility
{
    internal const string UnfinishedThingRejectionCode = "ClashOfRim.ThingTransfer.RejectUnfinished";
    internal const string ActiveQuestBookRejectionCode = "ClashOfRim.ThingTransfer.RejectActiveQuestBook";
    internal const string MetadataStyleDefName = "clashofrim.core.transfer.styleDefName";
    internal const string MetadataHatcherProgress = "clashofrim.core.transfer.hatcherProgress";
    internal const string MetadataBookHasQuest = "clashofrim.core.transfer.bookHasQuest";
    internal const string MetadataBookQuestDefName = "clashofrim.core.transfer.bookQuestDefName";

    private static readonly FieldInfo? HatcherProgressField = typeof(CompHatcher).GetField(
        "gestateProgress",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? QuestBookHasQuestField = typeof(BookOutcomeDoer_GiveQuest).GetField(
        "hasQuest",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? QuestBookQuestDefField = typeof(BookOutcomeDoer_GiveQuest).GetField(
        "questDef",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? QuestBookQuestField = typeof(BookOutcomeDoer_GiveQuest).GetField(
        "quest",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TargetableSelectedTargetField = typeof(CompTargetable).GetField(
        "selectedTarget",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ExplosiveInstigatorField = typeof(CompExplosive).GetField(
        "instigator",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? ExplosiveIgnoredThingsField = typeof(CompExplosive).GetField(
        "thingsIgnoredByExplosion",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterThingTransferRule(
            "clashofrim.core.thing-transfer",
            ValidateOutbound,
            CaptureMetadata,
            FinalizeInbound);
    }

    private static ThingTransferDecision ValidateOutbound(Thing thing, ThingTransferContext context)
    {
        if (context.Direction != ThingTransferDirection.Outbound)
        {
            return ThingTransferDecision.Allow();
        }

        if (thing is UnfinishedThing)
        {
            return ThingTransferDecision.Reject(UnfinishedThingRejectionCode);
        }

        BookOutcomeDoer_GiveQuest? questDoer = QuestDoer(thing);
        if (questDoer is not null && QuestBookQuestField?.GetValue(questDoer) is Quest)
        {
            return ThingTransferDecision.Reject(ActiveQuestBookRejectionCode);
        }

        return ThingTransferDecision.Allow();
    }

    private static void CaptureMetadata(
        Thing thing,
        ModThingReferenceDto reference,
        ThingTransferContext context)
    {
        ThingStyleDef? styleDef = thing.StyleDef;
        if (!string.IsNullOrWhiteSpace(styleDef?.defName))
        {
            reference.Metadata[MetadataStyleDefName] = styleDef!.defName;
        }

        if (thing.TryGetComp<CompHatcher>() is { } hatcher
            && HatcherProgressField?.GetValue(hatcher) is float progress)
        {
            reference.Metadata[MetadataHatcherProgress] = progress.ToString("R", CultureInfo.InvariantCulture);
        }

        BookOutcomeDoer_GiveQuest? questDoer = QuestDoer(thing);
        if (questDoer is null)
        {
            return;
        }

        bool hasQuest = QuestBookHasQuestField?.GetValue(questDoer) is true;
        reference.Metadata[MetadataBookHasQuest] = hasQuest ? "true" : "false";
        if (QuestBookQuestDefField?.GetValue(questDoer) is QuestScriptDef questDef
            && !string.IsNullOrWhiteSpace(questDef.defName))
        {
            reference.Metadata[MetadataBookQuestDefName] = questDef.defName;
        }
    }

    private static bool FinalizeInbound(
        ModThingReferenceDto reference,
        Thing thing,
        ThingTransferContext context,
        out string? missingDefName)
    {
        missingDefName = null;
        if (!ApplyStyle(reference, thing, out missingDefName)
            || !ApplyQuestBook(reference, thing, out missingDefName))
        {
            return false;
        }

        if (thing.TryGetComp<CompHatcher>() is { } hatcher)
        {
            if (reference.Metadata.TryGetValue(MetadataHatcherProgress, out string? progressText)
                && float.TryParse(progressText, NumberStyles.Float, CultureInfo.InvariantCulture, out float progress))
            {
                HatcherProgressField?.SetValue(hatcher, Math.Max(0f, Math.Min(1f, progress)));
            }

            hatcher.hatcheeParent = null;
            hatcher.otherParent = null;
            hatcher.hatcheeFaction = context.ReceivingFaction ?? Faction.OfPlayer;
        }

        if (thing.TryGetComp<CompTargetable>() is { } targetable)
        {
            TargetableSelectedTargetField?.SetValue(targetable, null);
        }

        if (thing.TryGetComp<CompExplosive>() is { } explosive)
        {
            ExplosiveInstigatorField?.SetValue(explosive, null);
            ExplosiveIgnoredThingsField?.SetValue(explosive, null);
        }

        return true;
    }

    private static bool ApplyStyle(
        ModThingReferenceDto reference,
        Thing thing,
        out string? missingDefName)
    {
        missingDefName = null;
        if (!reference.Metadata.TryGetValue(MetadataStyleDefName, out string? styleDefName)
            || string.IsNullOrWhiteSpace(styleDefName))
        {
            return true;
        }

        ThingStyleDef? styleDef = DefDatabase<ThingStyleDef>.GetNamedSilentFail(styleDefName!);
        if (styleDef is null)
        {
            missingDefName = styleDefName;
            return false;
        }

        thing.StyleSourcePrecept = null;
        thing.StyleDef = styleDef;
        return true;
    }

    private static bool ApplyQuestBook(
        ModThingReferenceDto reference,
        Thing thing,
        out string? missingDefName)
    {
        missingDefName = null;
        BookOutcomeDoer_GiveQuest? questDoer = QuestDoer(thing);
        if (questDoer is null
            || !reference.Metadata.TryGetValue(MetadataBookHasQuest, out string? hasQuestText))
        {
            return true;
        }

        QuestScriptDef? questDef = null;
        if (reference.Metadata.TryGetValue(MetadataBookQuestDefName, out string? questDefName)
            && !string.IsNullOrWhiteSpace(questDefName))
        {
            questDef = DefDatabase<QuestScriptDef>.GetNamedSilentFail(questDefName!);
            if (questDef is null)
            {
                missingDefName = questDefName;
                return false;
            }
        }

        QuestBookHasQuestField?.SetValue(questDoer, string.Equals(hasQuestText, "true", StringComparison.OrdinalIgnoreCase));
        QuestBookQuestDefField?.SetValue(questDoer, questDef);
        QuestBookQuestField?.SetValue(questDoer, null);
        return true;
    }

    private static BookOutcomeDoer_GiveQuest? QuestDoer(Thing thing)
    {
        return thing is Book book
            ? book.BookComp?.Doers.OfType<BookOutcomeDoer_GiveQuest>().FirstOrDefault()
            : null;
    }
}
