using AIRsLight.ClashOfRim.ClientNetwork;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static void RegisterThingTransferRule(
        string key,
        ThingTransferValidator? validator,
        ThingTransferMetadataCapturer? capturer,
        ThingTransferFinalizer? finalizer) =>
        ThingTransfers.RegisterThingTransferRule(key, validator, capturer, finalizer);

    public static bool CanTransferThing(
        Thing thing,
        ThingTransferContext context,
        out string? rejectionCode) =>
        ThingTransfers.CanTransferThing(thing, context, out rejectionCode);

    public static bool PrepareThingTransfer(
        Thing thing,
        ModThingReferenceDto reference,
        ThingTransferContext context,
        out string? rejectionCode) =>
        ThingTransfers.PrepareThingTransfer(thing, reference, context, out rejectionCode);

    public static bool FinalizeThingTransfer(
        ModThingReferenceDto reference,
        Thing thing,
        ThingTransferContext context,
        out string? missingDefName) =>
        ThingTransfers.FinalizeThingTransfer(reference, thing, context, out missingDefName);

    public static void RegisterThingReferenceEditor(
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator) =>
        ThingReferences.RegisterThingReferenceEditor(editor, cleaner, validator);

    public static void RegisterThingReferenceEditor(
        string key,
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator) =>
        ThingReferences.RegisterThingReferenceEditor(key, editor, cleaner, validator);

    public static void RegisterThingReferenceUniqueRequestPredicate(ThingReferenceUniqueRequestPredicate predicate) =>
        ThingReferences.RegisterThingReferenceUniqueRequestPredicate(predicate);

    public static void RegisterThingReferenceDefaultMetadataProvider(ThingReferenceDefaultMetadataProvider provider) =>
        ThingReferences.RegisterThingReferenceDefaultMetadataProvider(provider);

    public static void RegisterThingReferenceMetadata(
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider) =>
        ThingReferences.RegisterThingReferenceMetadata(appender, thingMatcher, dtoMatcher, applier, strictnessProvider);

    public static void RegisterThingReferenceMetadata(
        string key,
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider) =>
        ThingReferences.RegisterThingReferenceMetadata(key, appender, thingMatcher, dtoMatcher, applier, strictnessProvider);

    public static void RegisterThingReferenceDisplay(
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor) =>
        ThingReferences.RegisterThingReferenceDisplay(partsProvider, standardStatSuppressor);

    public static void RegisterThingReferenceDisplay(
        string key,
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor) =>
        ThingReferences.RegisterThingReferenceDisplay(key, partsProvider, standardStatSuppressor);

    public static void RegisterThingReferenceCacheKeyPartsProvider(ThingReferenceCacheKeyPartsProvider provider) =>
        ThingReferences.RegisterThingReferenceCacheKeyPartsProvider(provider);

    public static void RegisterThingReferenceMetadataNormalizer(ThingReferenceMetadataNormalizer normalizer) =>
        ThingReferences.RegisterThingReferenceMetadataNormalizer(normalizer);

    public static void RegisterThingReferenceThingFactory(ThingReferenceThingFactory factory) =>
        ThingReferences.RegisterThingReferenceThingFactory(factory);

    public static void RegisterThingReferenceThingFactory(string key, ThingReferenceThingFactory factory) =>
        ThingReferences.RegisterThingReferenceThingFactory(key, factory);

    public static bool TryMakeThingReferenceThing(ThingDef def, ThingDef? stuff, out Thing? thing) =>
        ThingReferences.TryMakeThingReferenceThing(def, stuff, out thing);

    public static void RegisterThingReferenceDefKindPredicate(string kind, ThingReferenceDefKindPredicate predicate) =>
        ThingReferences.RegisterThingReferenceDefKindPredicate(kind, predicate);

    public static bool HasThingReferenceDefKind(string kind, ThingDef? def) =>
        ThingReferences.HasThingReferenceDefKind(kind, def);

    public static void RegisterDefensePointDefNameProvider(DefensePointDefNameProvider provider) =>
        ThingReferences.RegisterDefensePointDefNameProvider(provider);

    public static IEnumerable<string> GetCompatibilityDefensePointDefNames() =>
        ThingReferences.GetCompatibilityDefensePointDefNames();

    public static bool TryDrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight) =>
        ThingReferences.TryDrawThingReferenceEditor(surface, def, item, rect, out consumedHeight);

    public static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item) =>
        ThingReferences.ClearThingReferenceMetadata(surface, def, item);

    public static void NormalizeThingReferenceMetadata(ModThingReferenceDto reference) =>
        ThingReferences.NormalizeThingReferenceMetadata(reference);

    public static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item) =>
        ThingReferences.IsThingReferenceComplete(surface, def, item);

    public static void ApplyThingReferenceDefaultMetadata(string surface, ThingDef? def, ModThingReferenceDto item) =>
        ThingReferences.ApplyThingReferenceDefaultMetadata(surface, def, item);

    public static bool IsThingReferenceUniqueRequest(ThingDef? def) =>
        ThingReferences.IsThingReferenceUniqueRequest(def);

    public static void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference) =>
        ThingReferences.AppendThingReferenceMetadata(metadataThing, reference);

    public static bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, Thing metadataThing) =>
        ThingReferences.ThingReferenceMetadataMatches(requirement, metadataThing);

    public static bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate) =>
        ThingReferences.ThingReferenceMetadataMatches(requirement, candidate);

    public static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName) =>
        ThingReferences.TryApplyThingReferenceMetadata(reference, thing, out missingDefName);

    public static int ThingReferenceMetadataStrictness(ModThingReferenceDto requirement) =>
        ThingReferences.ThingReferenceMetadataStrictness(requirement);

    public static void AppendThingReferenceDisplayParts(ModThingReferenceDto thing, bool asRequirement, List<string> parts) =>
        ThingReferences.AppendThingReferenceDisplayParts(thing, asRequirement, parts);

    public static bool SuppressesStandardThingStats(ThingDef? def) =>
        ThingReferences.SuppressesStandardThingStats(def);

    public static IEnumerable<string> ThingReferenceCacheKeyParts(ModThingReferenceDto thing) =>
        ThingReferences.ThingReferenceCacheKeyParts(thing);
}
