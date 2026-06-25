using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static void RegisterThingReferenceEditor(
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator)
    {
        string key = RegistrationKey(editor)
            + "|"
            + RegistrationKey(cleaner)
            + "|"
            + RegistrationKey(validator);
        RegisterThingReferenceEditor(key, editor, cleaner, validator);
    }

    public static void RegisterThingReferenceEditor(
        string key,
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator)
    {
        if (string.IsNullOrWhiteSpace(key)
            || editor is null
            || cleaner is null
            || validator is null)
        {
            return;
        }

        RegisterMetadataKeyedRegistration(
            ThingReferenceEditorRegistrations,
            new ThingReferenceEditorRegistration(key, editor, cleaner, validator),
            nameof(ThingReferenceEditorRegistration),
            key,
            "ThingReferenceEditorDuplicateRejected",
            "ThingReferenceEditorDuplicateReplaced");
    }

    public static void RegisterThingReferenceUniqueRequestPredicate(ThingReferenceUniqueRequestPredicate predicate)
    {
        RegisterUnique(ThingReferenceUniqueRequestPredicates, predicate);
    }

    public static void RegisterThingReferenceDefaultMetadataProvider(ThingReferenceDefaultMetadataProvider provider)
    {
        RegisterUnique(ThingReferenceDefaultMetadataProviders, provider);
    }

    public static void RegisterThingReferenceMetadata(
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider)
    {
        string key = RegistrationKey(appender)
            + "|"
            + RegistrationKey(thingMatcher)
            + "|"
            + RegistrationKey(dtoMatcher)
            + "|"
            + RegistrationKey(applier)
            + "|"
            + RegistrationKey(strictnessProvider);
        RegisterThingReferenceMetadata(
            key,
            appender,
            thingMatcher,
            dtoMatcher,
            applier,
            strictnessProvider);
    }

    public static void RegisterThingReferenceMetadata(
        string key,
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider)
    {
        if (string.IsNullOrWhiteSpace(key)
            || appender is null
            || thingMatcher is null
            || dtoMatcher is null
            || applier is null
            || strictnessProvider is null)
        {
            return;
        }

        RegisterMetadataKeyedRegistration(
            ThingReferenceMetadataRegistrations,
            new ThingReferenceMetadataRegistration(
                key,
                appender,
                thingMatcher,
                dtoMatcher,
                applier,
                strictnessProvider),
            nameof(ThingReferenceMetadataRegistration),
            key,
            "ThingReferenceMetadataDuplicateRejected",
            "ThingReferenceMetadataDuplicateReplaced");
    }

    public static void RegisterThingReferenceDisplay(
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor)
    {
        string key = RegistrationKey(partsProvider) + "|" + RegistrationKey(standardStatSuppressor);
        RegisterThingReferenceDisplay(key, partsProvider, standardStatSuppressor);
    }

    public static void RegisterThingReferenceDisplay(
        string key,
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor)
    {
        if (string.IsNullOrWhiteSpace(key)
            || partsProvider is null
            || standardStatSuppressor is null)
        {
            return;
        }

        RegisterMetadataKeyedRegistration(
            ThingReferenceDisplayRegistrations,
            new ThingReferenceDisplayRegistration(key, partsProvider, standardStatSuppressor),
            nameof(ThingReferenceDisplayRegistration),
            key,
            "ThingReferenceDisplayDuplicateRejected",
            "ThingReferenceDisplayDuplicateReplaced");
    }

    public static void RegisterThingReferenceCacheKeyPartsProvider(ThingReferenceCacheKeyPartsProvider provider)
    {
        RegisterUnique(ThingReferenceCacheKeyPartsProviders, provider);
    }

    public static void RegisterThingReferenceMetadataNormalizer(ThingReferenceMetadataNormalizer normalizer)
    {
        RegisterUnique(ThingReferenceMetadataNormalizers, normalizer);
    }

    public static void RegisterThingReferenceThingFactory(ThingReferenceThingFactory factory)
    {
        if (factory is null)
        {
            return;
        }

        RegisterThingReferenceThingFactory(RegistrationKey(factory), factory);
    }

    public static void RegisterThingReferenceThingFactory(string key, ThingReferenceThingFactory factory)
    {
        if (string.IsNullOrWhiteSpace(key) || factory is null)
        {
            return;
        }

        RegisterMetadataKeyedRegistration(
            ThingReferenceThingFactories,
            new ThingReferenceThingFactoryRegistration(key, factory),
            nameof(ThingReferenceThingFactoryRegistration),
            key,
            "ThingReferenceThingFactoryDuplicateRejected",
            "ThingReferenceThingFactoryDuplicateReplaced");
    }

    public static bool TryMakeThingReferenceThing(ThingDef def, ThingDef? stuff, out Thing? thing)
    {
        thing = null;
        if (def is null)
        {
            return false;
        }

        foreach (ThingReferenceThingFactoryRegistration registration in ThingReferenceThingFactories.ToList())
        {
            Thing? created = null;
            if (!TryInvokeCompatibilityCallback(
                    nameof(TryMakeThingReferenceThing),
                    registration,
                    () => registration.Factory(def, stuff, out created),
                    out bool handled))
            {
                continue;
            }

            if (handled && created is not null)
            {
                thing = created;
                return true;
            }
        }

        return false;
    }

    public static void RegisterThingReferenceDefKindPredicate(string kind, ThingReferenceDefKindPredicate predicate)
    {
        if (string.IsNullOrWhiteSpace(kind) || predicate is null)
        {
            return;
        }

        string normalizedKind = kind.Trim();
        var registration = new ThingReferenceDefKindPredicateRegistration(normalizedKind, predicate);
        ThingReferenceDefKindPredicates.Add(registration);
        int priority = ActiveRegistrationPriority();
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(ThingReferenceDefKindPredicates);
        CompatibilityRegistrationRecord record = AddRegistrationRecord(
            nameof(ThingReferenceDefKindPredicateRegistration),
            normalizedKind,
            priority);
        TrackRegistration(() =>
        {
            ThingReferenceDefKindPredicates.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
        });
    }

    public static bool HasThingReferenceDefKind(string kind, ThingDef? def)
    {
        if (def is null || string.IsNullOrWhiteSpace(kind))
        {
            return false;
        }

        string normalizedKind = kind.Trim();
        foreach (ThingReferenceDefKindPredicateRegistration registration in ThingReferenceDefKindPredicates.ToList())
        {
            if (!string.Equals(registration.Kind, normalizedKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryInvokeCompatibilityCallback(
                    nameof(HasThingReferenceDefKind),
                    registration,
                    () => registration.Predicate(def),
                    out bool matched))
            {
                continue;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    public static void RegisterDefensePointDefNameProvider(DefensePointDefNameProvider provider)
    {
        RegisterUnique(DefensePointDefNameProviders, provider);
    }

    public static IEnumerable<string> GetCompatibilityDefensePointDefNames()
    {
        foreach (DefensePointDefNameProvider provider in DefensePointDefNameProviders.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(GetCompatibilityDefensePointDefNames),
                    provider,
                    () => provider(),
                    out IEnumerable<string>? defNames))
            {
                continue;
            }

            foreach (string? defName in defNames ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(defName))
                {
                    yield return defName!.Trim();
                }
            }
        }
    }

    public static bool TryDrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        foreach (ThingReferenceEditorRegistration registration in ThingReferenceEditorRegistrations.ToList())
        {
            float height = 0f;
            if (!TryInvokeCompatibilityCallback(
                    nameof(TryDrawThingReferenceEditor),
                    registration,
                    () => registration.Editor(surface, def, item, rect, out height),
                    out bool handled))
            {
                continue;
            }

            if (handled)
            {
                consumedHeight = height;
                return true;
            }
        }

        consumedHeight = 0f;
        return false;
    }

    public static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        foreach (ThingReferenceEditorRegistration registration in ThingReferenceEditorRegistrations.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(ClearThingReferenceMetadata),
                registration,
                () => registration.Cleaner(surface, def, item));
        }
    }

    public static void NormalizeThingReferenceMetadata(ModThingReferenceDto reference)
    {
        if (reference is null)
        {
            return;
        }

        foreach (ThingReferenceMetadataNormalizer normalizer in ThingReferenceMetadataNormalizers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(NormalizeThingReferenceMetadata),
                normalizer,
                () => normalizer(reference));
        }
    }

    public static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        foreach (ThingReferenceEditorRegistration registration in ThingReferenceEditorRegistrations.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(IsThingReferenceComplete),
                    registration,
                    () => registration.Validator(surface, def, item),
                    out bool valid))
            {
                return false;
            }

            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    public static void ApplyThingReferenceDefaultMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (item is null)
        {
            return;
        }

        foreach (ThingReferenceDefaultMetadataProvider provider in ThingReferenceDefaultMetadataProviders.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(ApplyThingReferenceDefaultMetadata),
                provider,
                () => provider(surface, def, item));
        }
    }

    public static bool IsThingReferenceUniqueRequest(ThingDef? def)
    {
        foreach (ThingReferenceUniqueRequestPredicate predicate in ThingReferenceUniqueRequestPredicates.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(IsThingReferenceUniqueRequest),
                    predicate,
                    () => predicate(def),
                    out bool unique))
            {
                continue;
            }

            if (unique)
            {
                return true;
            }
        }

        return false;
    }

    public static void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(AppendThingReferenceMetadata),
                registration,
                () => registration.Appender(metadataThing, reference));
        }
    }

    public static bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(ThingReferenceMetadataMatches),
                    registration,
                    () => registration.ThingMatcher(requirement, metadataThing),
                    out bool matched))
            {
                return false;
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    public static bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(ThingReferenceMetadataMatches),
                    registration,
                    () => registration.DtoMatcher(requirement, candidate),
                    out bool matched))
            {
                return false;
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            string? localMissingDefName = null;
            if (!TryInvokeCompatibilityCallback(
                    nameof(TryApplyThingReferenceMetadata),
                    registration,
                    () => registration.Applier(reference, thing, out localMissingDefName),
                    out bool applied))
            {
                return false;
            }

            missingDefName = localMissingDefName;
            if (!applied)
            {
                return false;
            }
        }

        return true;
    }

    public static int ThingReferenceMetadataStrictness(ModThingReferenceDto requirement)
    {
        int strictness = 0;
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            if (TryInvokeCompatibilityCallback(
                    nameof(ThingReferenceMetadataStrictness),
                    registration,
                    () => registration.StrictnessProvider(requirement),
                    out int contribution))
            {
                strictness += contribution;
            }
        }

        return strictness;
    }

    public static void AppendThingReferenceDisplayParts(ModThingReferenceDto thing, bool asRequirement, List<string> parts)
    {
        if (parts is null)
        {
            return;
        }

        foreach (ThingReferenceDisplayRegistration registration in ThingReferenceDisplayRegistrationSnapshot())
        {
            TryInvokeCompatibilityCallback(
                nameof(AppendThingReferenceDisplayParts),
                registration,
                () => registration.PartsProvider(thing, asRequirement, parts));
        }
    }

    public static bool SuppressesStandardThingStats(ThingDef? def)
    {
        foreach (ThingReferenceDisplayRegistration registration in ThingReferenceDisplayRegistrationSnapshot())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(SuppressesStandardThingStats),
                    registration,
                    () => registration.StandardStatSuppressor(def),
                    out bool suppresses))
            {
                continue;
            }

            if (suppresses)
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> ThingReferenceCacheKeyParts(ModThingReferenceDto thing)
    {
        if (thing is null)
        {
            yield break;
        }

        foreach (ThingReferenceCacheKeyPartsProvider provider in ThingReferenceCacheKeyPartsProviderSnapshot())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(ThingReferenceCacheKeyParts),
                    provider,
                    () => provider(thing),
                    out IEnumerable<string>? providedParts))
            {
                continue;
            }

            foreach (string? part in providedParts ?? Enumerable.Empty<string>())
            {
                yield return part ?? string.Empty;
            }
        }
    }
}
