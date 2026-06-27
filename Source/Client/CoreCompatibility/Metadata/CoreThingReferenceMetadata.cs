using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.Traits;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

internal static class CoreThingReferenceMetadata
{
    public static BuiltInCoreCompatibilityPackageDescriptor Descriptor { get; } =
        new("clashofrim.core.thing-reference-metadata", Apply, order: 100);

    internal const string DefKindBook = "clashofrim.core.book";
    internal const string DefKindTechprint = "clashofrim.core.techprint";
    internal const string DefKindSkillBook = "clashofrim.core.skill-book";
    private const string MetadataBookSkillDefNames = "clashofrim.core.bookSkillDefNames";
    private const string MetadataTargetBookSkillDefName = "clashofrim.core.targetBookSkillDefName";
    private const string MetadataResearchProjectDefName = "clashofrim.core.researchProjectDefName";
    private const string MetadataTargetResearchProjectDefName = "clashofrim.core.targetResearchProjectDefName";
    private const string MetadataSourceLabels = "clashofrim.core.sourceLabels";
    private const string MetadataArtTitle = "clashofrim.core.artTitle";
    private const string MetadataArtAuthor = "clashofrim.core.artAuthor";
    private const string MetadataArtDescription = "clashofrim.core.artDescription";
    private const string MetadataOverrideGraphicIndex = "clashofrim.core.overrideGraphicIndex";
    private const string CoreThingReferenceEditorKey = "clashofrim.core.thing-reference-editor";
    private const string CoreThingReferenceMetadataKey = "clashofrim.core.thing-reference-metadata";
    private const string CoreThingReferenceDisplayKey = "clashofrim.core.thing-reference-display";
    private const string CoreThingFactoryKey = "clashofrim.core.thing-reference-factory";
    private static readonly FieldInfo? SkillBookValuesField =
        typeof(BookOutcomeDoerGainSkillExp).GetField("values", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ResearchBookValuesField =
        typeof(ReadingOutcomeDoerGainResearch).GetField("values", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? BookDescriptionInvalidatedField =
        typeof(Book).GetField("descCanBeInvalidated", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? HasSourcesField =
        typeof(CompHasSources).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CompArtAuthorNameField =
        typeof(CompArt).GetField("authorNameInt", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CompArtTaleRefField =
        typeof(CompArt).GetField("taleRef", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Dictionary<string, bool> SkillBookDefCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> ResearchBookDefCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? techprintProjectByThingDefName;
    private static IReadOnlyList<ResearchProjectDef>? cachedTechprintResearchProjects;
    private static IReadOnlyList<SkillDef>? cachedSkills;

    internal static IReadOnlyList<string> BookSkillDefNames(ModThingReferenceDto? reference) => MetadataList(reference, MetadataBookSkillDefNames);

    internal static void SetBookSkillDefNames(ModThingReferenceDto? reference, IEnumerable<string>? values) => SetMetadataList(reference, MetadataBookSkillDefNames, values);

    internal static string? TargetBookSkillDefName(ModThingReferenceDto? reference) => MetadataText(reference, MetadataTargetBookSkillDefName);

    internal static void SetTargetBookSkillDefName(ModThingReferenceDto? reference, string? value) => SetMetadataText(reference, MetadataTargetBookSkillDefName, value);

    internal static string? ResearchProjectDefName(ModThingReferenceDto? reference) => MetadataText(reference, MetadataResearchProjectDefName);

    internal static void SetResearchProjectDefName(ModThingReferenceDto? reference, string? value) => SetMetadataText(reference, MetadataResearchProjectDefName, value);

    internal static string? TargetResearchProjectDefName(ModThingReferenceDto? reference) => MetadataText(reference, MetadataTargetResearchProjectDefName);

    internal static void SetTargetResearchProjectDefName(ModThingReferenceDto? reference, string? value) => SetMetadataText(reference, MetadataTargetResearchProjectDefName, value);

    private static IReadOnlyList<string> SourceLabels(ModThingReferenceDto? reference) => MetadataLineList(reference, MetadataSourceLabels);

    private static void SetSourceLabels(ModThingReferenceDto? reference, IEnumerable<string>? values) => SetMetadataLineList(reference, MetadataSourceLabels, values);

    private static string? ArtTitle(ModThingReferenceDto? reference) => MetadataText(reference, MetadataArtTitle);

    private static void SetArtTitle(ModThingReferenceDto? reference, string? value) => SetMetadataText(reference, MetadataArtTitle, value);

    private static string? ArtAuthor(ModThingReferenceDto? reference) => MetadataText(reference, MetadataArtAuthor);

    private static void SetArtAuthor(ModThingReferenceDto? reference, string? value) => SetMetadataText(reference, MetadataArtAuthor, value);

    private static string? ArtDescription(ModThingReferenceDto? reference) => MetadataText(reference, MetadataArtDescription);

    private static void SetArtDescription(ModThingReferenceDto? reference, string? value) => SetMetadataText(reference, MetadataArtDescription, value);

    private static int? OverrideGraphicIndex(ModThingReferenceDto? reference) => MetadataInt(reference, MetadataOverrideGraphicIndex);

    private static void SetOverrideGraphicIndex(ModThingReferenceDto? reference, int? value) => SetMetadataInt(reference, MetadataOverrideGraphicIndex, value);

    private static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterThingReferenceThingFactory(CoreThingFactoryKey, TryMakeCoreThingReferenceThing);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDefKindPredicate(DefKindBook, IsBookDef);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDefKindPredicate(DefKindTechprint, IsTechprintDef);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDefKindPredicate(DefKindSkillBook, IsSkillBookDef);
        ClashOfRimCompatibilityApi.RegisterThingReferenceEditor(
            CoreThingReferenceEditorKey,
            DrawThingReferenceEditor,
            ClearThingReferenceMetadata,
            IsThingReferenceComplete);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDefaultMetadataProvider(ApplyDefaultThingReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadata(
            CoreThingReferenceMetadataKey,
            AppendThingReferenceMetadata,
            ThingReferenceMatches,
            ThingReferenceDtoMatches,
            TryApplyThingReferenceMetadata,
            ThingReferenceStrictness);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDisplay(
            CoreThingReferenceDisplayKey,
            AppendThingReferenceDisplayParts,
            SuppressesStandardThingStats);
        ClashOfRimCompatibilityApi.RegisterThingReferenceCacheKeyPartsProvider(ThingReferenceCacheKeyParts);
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadataNormalizer(SyncCoreMetadata);
    }

    private static bool DrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        consumedHeight = 0f;
        if (item is null)
        {
            return false;
        }

        if (string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal))
        {
            return DrawTradeRequestEditor(def, item, rect, out consumedHeight);
        }

        if (string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal))
        {
            return DrawServerShopEditor(def, item, rect, out consumedHeight);
        }

        return false;
    }

    private static bool DrawTradeRequestEditor(
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        consumedHeight = 0f;

        if (IsResearchBookDef(def))
        {
            DrawResearchProjectButton(new Rect(rect.x, rect.y, Math.Min(240f, rect.width), rect.height), item, requirementMode: true);
            consumedHeight = rect.height;
            return true;
        }

        if (IsSkillBookDef(def))
        {
            DrawBookSkillButton(new Rect(rect.x, rect.y, Math.Min(220f, rect.width), rect.height), item);
            consumedHeight = rect.height;
            return true;
        }

        return false;
    }

    private static bool DrawServerShopEditor(
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        consumedHeight = 0f;
        if (IsResearchBookDef(def))
        {
            DrawLabeledResearchProjectButton(rect, item, requirementMode: false);
            consumedHeight = 38f;
            return true;
        }

        if (IsSkillBookDef(def))
        {
            DrawLabeledBookSkillButton(rect, item);
            consumedHeight = 38f;
            return true;
        }

        if (TraitTrainerUtility.IsTraitTrainerDef(def))
        {
            DrawLabeledTraitButton(rect, item);
            consumedHeight = 38f;
            return true;
        }

        return false;
    }

    private static void DrawBookSkillButton(Rect rect, ModThingReferenceDto item)
    {
        string label = SkillLabel(TargetBookSkillDefName(item));
        if (!Widgets.ButtonText(rect, label))
        {
            return;
        }

        List<FloatMenuOption> options = new()
        {
            new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Any"), () => SetTargetBookSkillDefName(item, null))
        };
        foreach (SkillDef skill in SkillDefsInOrder())
        {
            SkillDef captured = skill;
            options.Add(new FloatMenuOption(captured.LabelCap, () => SetTargetBookSkillDefName(item, captured.defName)));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static void DrawLabeledBookSkillButton(Rect rect, ModThingReferenceDto item)
    {
        IReadOnlyList<string> bookSkillDefNames = BookSkillDefNames(item);
        string label = bookSkillDefNames.Count == 0
            ? ClashOfRimText.Key("ClashOfRim.Shop.SelectBookSkills")
            : string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), bookSkillDefNames.Select(SkillLabel));
        if (Widgets.ButtonText(new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f), label))
        {
            List<FloatMenuOption> options = new()
            {
                new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.None"), () => SetBookSkillDefNames(item, null))
            };
            foreach (SkillDef skill in SkillDefsInOrder())
            {
                SkillDef captured = skill;
                bool selected = bookSkillDefNames.Contains(captured.defName);
                options.Add(new FloatMenuOption(
                    (selected ? "[x] " : string.Empty) + captured.LabelCap,
                    () =>
                    {
                        List<string> updated = BookSkillDefNames(item).ToList();
                        if (updated.Contains(captured.defName))
                        {
                            updated.RemoveAll(defName => string.Equals(defName, captured.defName, StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            updated.Add(captured.defName);
                        }

                        SetBookSkillDefNames(item, updated);
                        SetTargetBookSkillDefName(item, null);
                    }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.BookContent"));
    }

    private static IReadOnlyList<SkillDef> SkillDefsInOrder()
    {
        return cachedSkills ??= DefDatabase<SkillDef>.AllDefsListForReading
            .OrderBy(skill => skill.label)
            .ThenBy(skill => skill.defName)
            .ToList();
    }

    private static void DrawResearchProjectButton(Rect rect, ModThingReferenceDto item, bool requirementMode)
    {
        string? projectDefName = requirementMode ? TargetResearchProjectDefName(item) : ResearchProjectDefName(item);
        string label = string.IsNullOrWhiteSpace(projectDefName)
            ? ClashOfRimText.Key("ClashOfRim.Trade.SelectResearchProject")
            : ResearchProjectLabel(projectDefName);
        if (Widgets.ButtonText(rect, label))
        {
            Find.WindowStack.Add(new TradeResearchProjectSelectionDialogWindow(item, requirementMode));
        }
    }

    private static void DrawLabeledResearchProjectButton(Rect rect, ModThingReferenceDto item, bool requirementMode)
    {
        DrawResearchProjectButton(new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f), item, requirementMode);
        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.ResearchProject"));
    }

    private static void DrawLabeledTraitButton(Rect rect, ModThingReferenceDto item)
    {
        string label = string.IsNullOrWhiteSpace(TraitTrainerUtility.TraitDefName(item))
            ? ClashOfRimText.Key("ClashOfRim.TraitTrainer.Select")
            : TraitTrainerUtility.TraitLabel(item);
        if (Widgets.ButtonText(new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f), label))
        {
            Find.WindowStack.Add(new TraitTrainerSelectionDialogWindow(item));
        }

        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.TraitTrainer.Trait"));
    }

    private static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (item is null)
        {
            return;
        }

        if (!IsResearchBookDef(def))
        {
            item.Metadata.Remove(MetadataResearchProjectDefName);
            item.Metadata.Remove(MetadataTargetResearchProjectDefName);
        }

        if (!IsBookDef(def))
        {
            item.Metadata.Remove(MetadataBookSkillDefNames);
            item.Metadata.Remove(MetadataTargetBookSkillDefName);
        }
        else if (!IsSkillBookDef(def))
        {
            item.Metadata.Remove(MetadataBookSkillDefNames);
            item.Metadata.Remove(MetadataTargetBookSkillDefName);
        }

        if (!TraitTrainerUtility.IsAnyTraitTrainerDef(def))
        {
            TraitTrainerUtility.ClearTrait(item);
        }
    }

    private static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (item is null)
        {
            return false;
        }

        SyncCoreMetadata(item);
        if (IsResearchBookDef(def))
        {
            string? project = string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal)
                ? TargetResearchProjectDefName(item)
                : ResearchProjectDefName(item);
            if (string.IsNullOrWhiteSpace(project))
            {
                return false;
            }
        }

        if (string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal))
        {
            if (IsSkillBookDef(def) && BookSkillDefNames(item).Count == 0)
            {
                return false;
            }

            if (TraitTrainerUtility.IsTraitTrainerDef(def)
                && (string.IsNullOrWhiteSpace(TraitTrainerUtility.TraitDefName(item))
                    || TraitTrainerUtility.TraitDegree(item) is null))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyDefaultThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (item is null || !IsResearchBookDef(def))
        {
            return;
        }

        if (string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal))
        {
            SetTargetResearchProjectDefName(item, null);
            return;
        }

        if (string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal))
        {
            SetResearchProjectDefName(item, null);
        }
    }

    private static void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        SyncCoreMetadata(reference);
        AppendOverrideGraphicIndex(metadataThing, reference);
        AppendSourceLabels(metadataThing, reference);
        AppendArtMetadata(metadataThing, reference);
        if (metadataThing is Book book)
        {
            SetBookSkillDefNames(reference, BookSkillDefNames(book));
            SetResearchProjectDefName(reference, BookResearchProjectDefNames(book).FirstOrDefault());
        }

        if (!TraitTrainerUtility.IsAnyTraitTrainerDef(metadataThing?.def)
            || metadataThing.TryGetComp<CompUseEffectAddTrait>() is not { } traitEffect
            || string.IsNullOrWhiteSpace(traitEffect.traitDefName))
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>(StringComparer.Ordinal);
        reference.Metadata[TraitTrainerUtility.MetadataTraitDefName] = traitEffect.traitDefName;
        reference.Metadata[TraitTrainerUtility.MetadataTraitDegree] = traitEffect.traitDegree.ToString();
    }

    private static bool ThingReferenceMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        SyncCoreMetadata(requirement);
        Book? candidateBook = metadataThing as Book;
        if (!BookRequirementMatches(requirement, BookSkillDefNames(candidateBook)))
        {
            return false;
        }

        if (!ResearchProjectRequirementMatches(requirement, ResearchProjectDefNames(metadataThing)))
        {
            return false;
        }

        if (!OverrideGraphicIndexRequirementMatches(requirement, metadataThing.OverrideGraphicIndex))
        {
            return false;
        }

        if (!ArtRequirementMatches(
                requirement,
                ArtTitle(metadataThing),
                ArtAuthor(metadataThing),
                ArtDescription(metadataThing)))
        {
            return false;
        }

        if (!TraitTrainerUtility.IsTraitTrainerReference(requirement)
            && !TraitTrainerUtility.IsRandomTraitTrainerReference(requirement))
        {
            return true;
        }

        string? requiredTrait = TraitTrainerUtility.TraitDefName(requirement);
        int? requiredDegree = TraitTrainerUtility.TraitDegree(requirement);
        if (string.IsNullOrWhiteSpace(requiredTrait) || requiredDegree is null)
        {
            return true;
        }

        CompUseEffectAddTrait? traitEffect = metadataThing.TryGetComp<CompUseEffectAddTrait>();
        return traitEffect is not null
            && string.Equals(traitEffect.traitDefName, requiredTrait, StringComparison.OrdinalIgnoreCase)
            && traitEffect.traitDegree == requiredDegree.Value;
    }

    private static bool ThingReferenceDtoMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        SyncCoreMetadata(requirement);
        SyncCoreMetadata(candidate);
        if (!BookRequirementMatches(requirement, BookSkillDefNames(candidate)))
        {
            return false;
        }

        if (!ResearchProjectRequirementMatches(requirement, ResearchProjectDefName(candidate)))
        {
            return false;
        }

        if (!OverrideGraphicIndexRequirementMatches(requirement, OverrideGraphicIndex(candidate)))
        {
            return false;
        }

        if (!ArtRequirementMatches(
                requirement,
                ArtTitle(candidate),
                ArtAuthor(candidate),
                ArtDescription(candidate)))
        {
            return false;
        }

        if (!TraitTrainerUtility.IsTraitTrainerReference(requirement)
            && !TraitTrainerUtility.IsRandomTraitTrainerReference(requirement))
        {
            return true;
        }

        string? requiredTrait = TraitTrainerUtility.TraitDefName(requirement);
        int? requiredDegree = TraitTrainerUtility.TraitDegree(requirement);
        if (string.IsNullOrWhiteSpace(requiredTrait) || requiredDegree is null)
        {
            return true;
        }

        return string.Equals(TraitTrainerUtility.TraitDefName(candidate), requiredTrait, StringComparison.OrdinalIgnoreCase)
            && TraitTrainerUtility.TraitDegree(candidate) == requiredDegree.Value;
    }

    private static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        SyncCoreMetadata(reference);
        missingDefName = null;
        if (!TryApplyBookMetadata(reference, thing, out missingDefName))
        {
            return false;
        }

        ApplySourceLabels(reference, thing);
        ApplyOverrideGraphicIndex(reference, thing);
        ApplyArtMetadata(reference, thing);

        if (!TraitTrainerUtility.IsAnyTraitTrainerDef(thing?.def))
        {
            return true;
        }

        string? traitDefName = TraitTrainerUtility.TraitDefName(reference);
        int? traitDegree = TraitTrainerUtility.TraitDegree(reference);
        if (TraitTrainerUtility.IsRandomTraitTrainerDef(thing?.def)
            && (string.IsNullOrWhiteSpace(traitDefName) || traitDegree is null))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(traitDefName)
            || traitDegree is null
            || TraitTrainerUtility.ResolveTraitDef(traitDefName) is null)
        {
            missingDefName = traitDefName ?? TraitTrainerUtility.ThingDefName;
            return false;
        }

        CompUseEffectAddTrait? traitEffect = thing.TryGetComp<CompUseEffectAddTrait>();
        if (traitEffect is null)
        {
            missingDefName = TraitTrainerUtility.ThingDefName;
            return false;
        }

        traitEffect.traitDefName = traitDefName;
        traitEffect.traitDegree = traitDegree.Value;
        return true;
    }

    private static int ThingReferenceStrictness(ModThingReferenceDto requirement)
    {
        SyncCoreMetadata(requirement);
        int bookRank = string.IsNullOrWhiteSpace(TargetBookSkillDefName(requirement)) ? 0 : 750_000;
        int researchProjectRank = string.IsNullOrWhiteSpace(TargetResearchProjectDefName(requirement)) ? 0 : 650_000;
        int graphicRank = OverrideGraphicIndex(requirement).HasValue ? 300_000 : 0;
        int traitRank = TraitTrainerUtility.IsTraitTrainerReference(requirement)
            && !string.IsNullOrWhiteSpace(TraitTrainerUtility.TraitDefName(requirement))
                ? 800_000
                : 0;
        return bookRank + researchProjectRank + graphicRank + traitRank;
    }

    private static void AppendThingReferenceDisplayParts(ModThingReferenceDto thing, bool asRequirement, List<string> parts)
    {
        string? targetBookSkillDefName = TargetBookSkillDefName(thing);
        string? targetResearchProjectDefName = TargetResearchProjectDefName(thing);
        IReadOnlyList<string> bookSkillDefNames = BookSkillDefNames(thing);
        string? researchProjectDefName = ResearchProjectDefName(thing);
        IReadOnlyList<string> sourceLabels = SourceLabels(thing);
        if (asRequirement && !string.IsNullOrWhiteSpace(targetBookSkillDefName))
        {
            parts.Add(ClashOfRimText.Key(
                "ClashOfRim.Trade.TargetBookSkillRequirement",
                SkillLabel(targetBookSkillDefName).Named("SKILL")));
        }
        else if (asRequirement && !string.IsNullOrWhiteSpace(targetResearchProjectDefName))
        {
            parts.Add(ClashOfRimText.Key(
                "ClashOfRim.Trade.TargetResearchProjectRequirement",
                ResearchProjectLabel(targetResearchProjectDefName).Named("PROJECT")));
        }
        else if (!asRequirement && bookSkillDefNames.Count > 0)
        {
            parts.Add(string.Join(
                ClashOfRimText.Key("ClashOfRim.ListSeparator"),
                bookSkillDefNames.Select(SkillLabel)));
        }
        else if (!asRequirement && !string.IsNullOrWhiteSpace(researchProjectDefName))
        {
            parts.Add(ResearchProjectLabel(researchProjectDefName));
        }

        if (!TradeUiUtility.SuppressDisplayOnlyMetadataParts && sourceLabels.Count > 0)
        {
            parts.Add(ClashOfRimText.Key(
                "ClashOfRim.Trade.SourceLabels",
                FormatSourceLabels(sourceLabels).Named("SOURCES")));
        }

        string? artTitle = ArtTitle(thing);
        if (!TradeUiUtility.SuppressDisplayOnlyMetadataParts && !asRequirement && !string.IsNullOrWhiteSpace(artTitle))
        {
            parts.Add(ClashOfRimText.Key(
                "ClashOfRim.Trade.ArtTitle",
                artTitle.Named("TITLE")));
        }
    }

    private static bool SuppressesStandardThingStats(ThingDef? def)
    {
        return false;
    }

    private static IEnumerable<string> ThingReferenceCacheKeyParts(ModThingReferenceDto thing)
    {
        SyncCoreMetadata(thing);
        if (!TraitTrainerUtility.IsTraitTrainerReference(thing)
            && !TraitTrainerUtility.IsRandomTraitTrainerReference(thing))
        {
            yield return string.Join(",", BookSkillDefNames(thing));
            yield return TargetBookSkillDefName(thing) ?? string.Empty;
            yield return ResearchProjectDefName(thing) ?? string.Empty;
            yield return TargetResearchProjectDefName(thing) ?? string.Empty;
            yield return string.Join("\n", SourceLabels(thing));
            yield return ArtTitle(thing) ?? string.Empty;
            yield return ArtAuthor(thing) ?? string.Empty;
            yield return ArtDescription(thing) ?? string.Empty;
            yield return OverrideGraphicIndex(thing)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            yield break;
        }

        yield return string.Join(",", BookSkillDefNames(thing));
        yield return TargetBookSkillDefName(thing) ?? string.Empty;
        yield return ResearchProjectDefName(thing) ?? string.Empty;
        yield return TargetResearchProjectDefName(thing) ?? string.Empty;
        yield return string.Join("\n", SourceLabels(thing));
        yield return ArtTitle(thing) ?? string.Empty;
        yield return ArtAuthor(thing) ?? string.Empty;
        yield return ArtDescription(thing) ?? string.Empty;
        yield return OverrideGraphicIndex(thing)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        yield return TraitTrainerUtility.TraitDefName(thing) ?? string.Empty;
        yield return TraitTrainerUtility.TraitDegree(thing)?.ToString() ?? string.Empty;
    }

    private static void AppendOverrideGraphicIndex(Thing? metadataThing, ModThingReferenceDto reference)
    {
        SetOverrideGraphicIndex(reference, metadataThing?.OverrideGraphicIndex);
    }

    private static void ApplyOverrideGraphicIndex(ModThingReferenceDto reference, Thing? thing)
    {
        int? graphicIndex = OverrideGraphicIndex(reference);
        if (thing is null || !graphicIndex.HasValue)
        {
            return;
        }

        thing.overrideGraphicIndex = graphicIndex.Value;
    }

    private static bool OverrideGraphicIndexRequirementMatches(ModThingReferenceDto requirement, int? candidateGraphicIndex)
    {
        int? requiredGraphicIndex = OverrideGraphicIndex(requirement);
        return !requiredGraphicIndex.HasValue || requiredGraphicIndex == candidateGraphicIndex;
    }

    private static void AppendArtMetadata(Thing? metadataThing, ModThingReferenceDto reference)
    {
        if (metadataThing?.TryGetComp<CompArt>() is not { } comp || !comp.Active)
        {
            return;
        }

        SetArtTitle(reference, SafeArtTitle(comp));
        SetArtAuthor(reference, SafeArtAuthor(comp));
        SetArtDescription(reference, SafeArtDescription(comp));
    }

    private static void ApplyArtMetadata(ModThingReferenceDto reference, Thing? thing)
    {
        string? title = ArtTitle(reference);
        string? author = ArtAuthor(reference);
        string? description = ArtDescription(reference);
        if (string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(author)
            && string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        if (thing?.TryGetComp<CompArt>() is not { } comp)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            comp.Title = title!;
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            CompArtAuthorNameField?.SetValue(comp, (TaggedString)author!);
        }

        CompArtTaleRefField?.SetValue(comp, TaleReference.Taleless);
        CompArtFixedText.Set(comp, description);
    }

    private static string? ArtTitle(Thing? thing)
    {
        return thing?.TryGetComp<CompArt>() is { } comp && comp.Active
            ? SafeArtTitle(comp)
            : null;
    }

    private static string? ArtAuthor(Thing? thing)
    {
        return thing?.TryGetComp<CompArt>() is { } comp && comp.Active
            ? SafeArtAuthor(comp)
            : null;
    }

    private static string? ArtDescription(Thing? thing)
    {
        return thing?.TryGetComp<CompArt>() is { } comp && comp.Active
            ? SafeArtDescription(comp)
            : null;
    }

    private static string? SafeArtTitle(CompArt comp)
    {
        try
        {
            return NormalizeText(comp.Title);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeArtAuthor(CompArt comp)
    {
        try
        {
            return NormalizeText(comp.AuthorName.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeArtDescription(CompArt comp)
    {
        string? fixedDescription = CompArtFixedText.Get(comp);
        if (!string.IsNullOrWhiteSpace(fixedDescription))
        {
            return fixedDescription;
        }

        try
        {
            return NormalizeText(comp.GenerateImageDescription().ToString());
        }
        catch
        {
            return null;
        }
    }

    private static bool ArtRequirementMatches(
        ModThingReferenceDto requirement,
        string? candidateTitle,
        string? candidateAuthor,
        string? candidateDescription)
    {
        string? requiredTitle = ArtTitle(requirement);
        string? requiredAuthor = ArtAuthor(requirement);
        string? requiredDescription = ArtDescription(requirement);
        return TextRequirementMatches(requiredTitle, candidateTitle)
            && TextRequirementMatches(requiredAuthor, candidateAuthor)
            && TextRequirementMatches(requiredDescription, candidateDescription);
    }

    private static bool TextRequirementMatches(string? required, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(required))
        {
            return true;
        }

        return string.Equals(required!.Trim(), candidate?.Trim(), StringComparison.Ordinal);
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value!.Trim();
    }

    private static void AppendSourceLabels(Thing? metadataThing, ModThingReferenceDto reference)
    {
        if (metadataThing?.TryGetComp<CompHasSources>() is not { } comp)
        {
            return;
        }

        SetSourceLabels(reference, SourceLabels(comp));
    }

    private static void ApplySourceLabels(ModThingReferenceDto reference, Thing? thing)
    {
        IReadOnlyList<string> labels = SourceLabels(reference);
        if (labels.Count == 0 || thing?.TryGetComp<CompHasSources>() is not { } comp)
        {
            return;
        }

        foreach (string label in labels)
        {
            comp.AddSource(label);
        }
    }

    private static IReadOnlyList<string> SourceLabels(Thing? thing)
    {
        return thing?.TryGetComp<CompHasSources>() is { } comp
            ? SourceLabels(comp)
            : Array.Empty<string>();
    }

    private static IReadOnlyList<string> SourceLabels(CompHasSources comp)
    {
        if (HasSourcesField?.GetValue(comp) is not List<string> labels || labels.Count == 0)
        {
            return Array.Empty<string>();
        }

        return labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string FormatSourceLabels(IReadOnlyList<string> sourceLabels)
    {
        const int maxVisibleLabels = 3;
        List<string> visible = sourceLabels
            .Take(maxVisibleLabels)
            .ToList();
        if (sourceLabels.Count > maxVisibleLabels)
        {
            visible.Add("...");
        }

        return string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), visible);
    }

    private static bool BookRequirementMatches(ModThingReferenceDto requirement, IReadOnlyCollection<string>? candidateSkillDefNames)
    {
        string? targetBookSkillDefName = TargetBookSkillDefName(requirement);
        if (string.IsNullOrWhiteSpace(targetBookSkillDefName))
        {
            return true;
        }

        return candidateSkillDefNames is not null
            && candidateSkillDefNames.Any(skillDefName => string.Equals(
                skillDefName,
                targetBookSkillDefName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool ResearchProjectRequirementMatches(ModThingReferenceDto requirement, string? candidateResearchProjectDefName)
    {
        return ResearchProjectRequirementMatches(
            requirement,
            string.IsNullOrWhiteSpace(candidateResearchProjectDefName)
                ? Array.Empty<string>()
                : new[] { candidateResearchProjectDefName! });
    }

    private static bool ResearchProjectRequirementMatches(ModThingReferenceDto requirement, IReadOnlyCollection<string> candidateResearchProjectDefNames)
    {
        string? targetResearchProjectDefName = TargetResearchProjectDefName(requirement);
        if (string.IsNullOrWhiteSpace(targetResearchProjectDefName))
        {
            return true;
        }

        return candidateResearchProjectDefNames.Any(candidate =>
            string.Equals(targetResearchProjectDefName, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryApplyBookMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        return TryApplySkillBookMetadata(reference, thing, out missingDefName)
            && TryApplyResearchBookMetadata(reference, thing, out missingDefName);
    }

    private static bool TryApplySkillBookMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        if (thing is not Book book
            || BookSkillDefNames(reference).Count == 0)
        {
            return true;
        }

        if (!book.BookComp.TryGetDoer(out BookOutcomeDoerGainSkillExp doer)
            || SkillBookValuesField?.GetValue(doer) is not Dictionary<SkillDef, float> values)
        {
            missingDefName = thing.def?.defName;
            return false;
        }

        QualityCategory quality;
        if (!QualityUtility.TryGetQuality(book, out quality))
        {
            quality = QualityCategory.Normal;
        }

        List<SkillDef> skills = BookSkillDefNames(reference)
            .Select(ResolveSkillDef)
            .Where(skill => skill is not null)
            .Cast<SkillDef>()
            .Distinct()
            .ToList();
        if (skills.Count == 0)
        {
            return true;
        }

        float value = BookUtility.GetSkillExpForQuality(quality);
        if (skills.Count > 1)
        {
            value *= 1.25f;
        }

        value /= skills.Count;
        values.Clear();
        foreach (SkillDef skill in skills)
        {
            values[skill] = value;
        }

        return true;
    }

    private static bool TryApplyResearchBookMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        string? projectDefName = ResearchProjectDefName(reference);
        if (thing is not Book book || string.IsNullOrWhiteSpace(projectDefName))
        {
            return true;
        }

        ResearchProjectDef? project = ResolveResearchProjectDef(projectDefName);
        if (project is null)
        {
            missingDefName = projectDefName;
            return false;
        }

        if (!book.BookComp.TryGetDoer(out ReadingOutcomeDoerGainResearch doer)
            || ResearchBookValuesField?.GetValue(doer) is not Dictionary<ResearchProjectDef, float> values)
        {
            missingDefName = thing.def?.defName;
            return false;
        }

        QualityCategory quality;
        if (!QualityUtility.TryGetQuality(book, out quality))
        {
            quality = QualityCategory.Normal;
        }

        values.Clear();
        values[project] = BookUtility.GetResearchExpForQuality(quality);
        BookDescriptionInvalidatedField?.SetValue(book, true);
        return true;
    }

    private static bool TryMakeCoreThingReferenceThing(ThingDef def, ThingDef? stuff, out Thing? thing)
    {
        if (!IsBookDef(def))
        {
            thing = null;
            return false;
        }

        thing = BookUtility.MakeBook(def, ArtGenerationContext.Outsider);
        return true;
    }

    internal static bool IsBookDef(ThingDef? def)
    {
        return def is not null && def.HasComp<CompBook>();
    }

    internal static bool IsTechprintDef(ThingDef? def)
    {
        if (def is null)
        {
            return false;
        }

        return def.HasComp(typeof(CompTechprint))
            || def.GetCompProperties<CompProperties_Techprint>() is not null
            || def.tradeTags?.Contains("Techprint") == true
            || TechprintProjectByThingDefName().ContainsKey(def.defName);
    }

    internal static string? TechprintProjectDefName(ThingDef? def)
    {
        if (def is null)
        {
            return null;
        }

        return def.GetCompProperties<CompProperties_Techprint>()?.project?.defName
            ?? (TechprintProjectByThingDefName().TryGetValue(def.defName, out string projectDefName) ? projectDefName : null);
    }

    internal static string ResearchProjectLabel(string? defName)
    {
        ResearchProjectDef? project = ResolveResearchProjectDef(defName);
        return project?.LabelCap ?? defName ?? ClashOfRimText.Key("ClashOfRim.Any");
    }

    internal static ResearchProjectDef? ResolveResearchProjectDef(string? defName)
    {
        return string.IsNullOrWhiteSpace(defName)
            ? null
            : DefDatabase<ResearchProjectDef>.GetNamedSilentFail(defName);
    }

    internal static IReadOnlyList<ResearchProjectDef> TechprintResearchProjects()
    {
        return cachedTechprintResearchProjects ??= DefDatabase<ResearchProjectDef>.AllDefsListForReading
            .Where(project => project.TechprintCount > 0)
            .Where(project => SafeTechprint(project) is not null)
            .OrderBy(project => project.label)
            .ThenBy(project => project.defName)
            .ToList();
    }

    private static Dictionary<string, string> TechprintProjectByThingDefName()
    {
        if (techprintProjectByThingDefName is not null)
        {
            return techprintProjectByThingDefName;
        }

        techprintProjectByThingDefName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ResearchProjectDef project in TechprintResearchProjects())
        {
            ThingDef? techprint = SafeTechprint(project);
            if (techprint is not null && !string.IsNullOrWhiteSpace(techprint.defName))
            {
                techprintProjectByThingDefName[techprint.defName] = project.defName;
            }
        }

        return techprintProjectByThingDefName;
    }

    private static ThingDef? SafeTechprint(ResearchProjectDef project)
    {
        try
        {
            return project.Techprint;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<ResearchProjectDef> ResearchProjectsForReference(ModThingReferenceDto? reference)
    {
        ThingDef? def = reference is null ? null : TradeThingReferenceUtility.ResolveReferenceDef(reference);
        return def is not null && IsResearchBookDef(def)
            ? ResearchBookProjects(def)
            : TechprintResearchProjects();
    }

    internal static ThingDef? ProjectThingDefForReference(ModThingReferenceDto? reference, ResearchProjectDef project)
    {
        ThingDef? def = reference is null ? null : TradeThingReferenceUtility.ResolveReferenceDef(reference);
        return IsResearchBookDef(def)
            ? def
            : SafeTechprint(project);
    }

    internal static bool IsSkillBookDef(ThingDef? def)
    {
        if (!IsBookDef(def))
        {
            return false;
        }

        if (SkillBookDefCache.TryGetValue(def!.defName, out bool cached))
        {
            return cached;
        }

        bool result;
        try
        {
            result = BookUtility.MakeBook(def, ArtGenerationContext.Outsider) is Book book
                && book.BookComp.TryGetDoer<BookOutcomeDoerGainSkillExp>(out _);
        }
        catch
        {
            result = false;
        }

        SkillBookDefCache[def.defName] = result;
        return result;
    }

    private static bool IsResearchBookDef(ThingDef? def)
    {
        if (!IsBookDef(def))
        {
            return false;
        }

        if (ResearchBookDefCache.TryGetValue(def!.defName, out bool cached))
        {
            return cached;
        }

        bool result;
        try
        {
            result = BookUtility.MakeBook(def, ArtGenerationContext.Outsider) is Book book
                && book.BookComp.TryGetDoer<ReadingOutcomeDoerGainResearch>(out _);
        }
        catch
        {
            result = false;
        }

        ResearchBookDefCache[def.defName] = result;
        return result;
    }

    internal static string SkillLabel(string? defName)
    {
        SkillDef? skill = ResolveSkillDef(defName);
        return skill?.label?.CapitalizeFirst() ?? defName ?? ClashOfRimText.Key("ClashOfRim.Any");
    }

    internal static SkillDef? ResolveSkillDef(string? defName)
    {
        return string.IsNullOrWhiteSpace(defName)
            ? null
            : DefDatabase<SkillDef>.GetNamedSilentFail(defName);
    }

    private static IReadOnlyList<string> BookSkillDefNames(Book? book)
    {
        if (book is null || !book.BookComp.TryGetDoer(out BookOutcomeDoerGainSkillExp doer))
        {
            return Array.Empty<string>();
        }

        return doer.Values.Keys
            .Select(skill => skill.defName)
            .Where(defName => !string.IsNullOrWhiteSpace(defName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BookResearchProjectDefNames(Book? book)
    {
        if (book is null
            || !book.BookComp.TryGetDoer(out ReadingOutcomeDoerGainResearch doer)
            || ResearchBookValuesField?.GetValue(doer) is not Dictionary<ResearchProjectDef, float> values)
        {
            return Array.Empty<string>();
        }

        return values.Keys
            .Select(project => project.defName)
            .Where(defName => !string.IsNullOrWhiteSpace(defName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ResearchProjectDefNames(Thing? thing)
    {
        if (thing is Book book)
        {
            return BookResearchProjectDefNames(book);
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<ResearchProjectDef> ResearchBookProjects(ThingDef def)
    {
        if (!IsResearchBookDef(def))
        {
            return Array.Empty<ResearchProjectDef>();
        }

        List<BookOutcomeProperties_GainResearch> researchDoers = def.GetCompProperties<CompProperties_Book>()?.doers
            ?.OfType<BookOutcomeProperties_GainResearch>()
            .ToList() ?? new List<BookOutcomeProperties_GainResearch>();
        if (researchDoers.Count == 0)
        {
            return Array.Empty<ResearchProjectDef>();
        }

        IEnumerable<ResearchProjectDef> projects = DefDatabase<ResearchProjectDef>.AllDefsListForReading
            .Where(project => researchDoers.Any(doer => IsValidResearchBookProject(doer, project)));
        return projects
            .OrderBy(project => project.label)
            .ThenBy(project => project.defName)
            .ToList();
    }

    private static bool IsValidResearchBookProject(BookOutcomeProperties_GainResearch doer, ResearchProjectDef project)
    {
        if (project is null
            || project.generalRules is null
            || project.TechprintCount > 0
            || (doer.ignoreZeroBaseCost && project.baseCost == 0f))
        {
            return false;
        }

        if (doer.exclude?.Any(item => item?.project == project) == true)
        {
            return false;
        }

        if (doer.include?.Count > 0)
        {
            return doer.include.Any(item => item?.project == project);
        }

        return doer.tabs is null
            || doer.tabs.Count == 0
            || doer.tabs.Any(item => item?.tab == project.tab);
    }

    private static void SyncCoreMetadata(ModThingReferenceDto? reference)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
    }

    private static IReadOnlyList<string> MetadataList(ModThingReferenceDto? reference, string key)
    {
        SyncCoreMetadata(reference);
        if (reference is null
            || !reference.Metadata.TryGetValue(key, out string? value)
            || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value!
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void SetMetadataList(ModThingReferenceDto? reference, string key, IEnumerable<string>? values)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
        string[] normalized = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = string.Join(",", normalized);
    }

    private static IReadOnlyList<string> MetadataLineList(ModThingReferenceDto? reference, string key)
    {
        SyncCoreMetadata(reference);
        if (reference is null
            || !reference.Metadata.TryGetValue(key, out string? value)
            || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value!
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void SetMetadataLineList(ModThingReferenceDto? reference, string key, IEnumerable<string>? values)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
        string[] normalized = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = string.Join("\n", normalized);
    }

    private static string? MetadataText(ModThingReferenceDto? reference, string key)
    {
        SyncCoreMetadata(reference);
        return reference is not null
            && reference.Metadata.TryGetValue(key, out string? value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }

    private static int? MetadataInt(ModThingReferenceDto? reference, string key)
    {
        string? value = MetadataText(reference, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static void SetMetadataInt(ModThingReferenceDto? reference, string key, int? value)
    {
        SetMetadataText(reference, key, value?.ToString(CultureInfo.InvariantCulture));
    }

    private static void SetMetadataText(ModThingReferenceDto? reference, string key, string? value)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(value))
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = value;
    }
}
