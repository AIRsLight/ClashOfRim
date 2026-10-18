using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using UnityEngine;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things;

internal sealed class ThingReferenceCompatibility
{
    private readonly List<ThingReferenceEditorRegistration> ThingReferenceEditorRegistrations = new();
    private readonly List<ThingReferenceDefaultMetadataProvider> ThingReferenceDefaultMetadataProviders = new();
    private readonly List<ThingReferenceUniqueRequestPredicate> ThingReferenceUniqueRequestPredicates = new();
    private readonly List<ThingReferenceMetadataRegistration> ThingReferenceMetadataRegistrations = new();
    private readonly List<ThingReferenceDisplayRegistration> ThingReferenceDisplayRegistrations = new();
    private readonly List<ThingReferenceCacheKeyPartsProvider> ThingReferenceCacheKeyPartsProviders = new();
    private readonly List<ThingReferenceMetadataNormalizer> ThingReferenceMetadataNormalizers = new();
    private readonly List<ThingReferenceThingFactoryRegistration> ThingReferenceThingFactories = new();
    private readonly List<ThingReferenceDefKindPredicateRegistration> ThingReferenceDefKindPredicates = new();
    private readonly List<DefensePointDefNameProvider> DefensePointDefNameProviders = new();
    private readonly CompatibilityRegistrationScope Registrations;
    private ThingReferenceDisplayRegistration[]? cachedThingReferenceDisplayRegistrations;
    private ThingReferenceCacheKeyPartsProvider[]? cachedThingReferenceCacheKeyPartsProviders;

    internal ThingReferenceCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterThingReferenceEditor(
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator)
    {
        string key = CompatibilityRegistrationScope.RegistrationKey(editor)
            + "|"
            + CompatibilityRegistrationScope.RegistrationKey(cleaner)
            + "|"
            + CompatibilityRegistrationScope.RegistrationKey(validator);
        RegisterThingReferenceEditor(key, editor, cleaner, validator);
    }

    internal void RegisterThingReferenceEditor(
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

        Registrations.RegisterMetadataKeyedRegistration(
            ThingReferenceEditorRegistrations,
            new ThingReferenceEditorRegistration(key, editor, cleaner, validator),
            nameof(ThingReferenceEditorRegistration),
            key,
            "ThingReferenceEditorDuplicateRejected",
            "ThingReferenceEditorDuplicateReplaced");
    }

    internal void RegisterThingReferenceUniqueRequestPredicate(ThingReferenceUniqueRequestPredicate predicate)
    {
        Registrations.RegisterUnique(ThingReferenceUniqueRequestPredicates, predicate);
    }

    internal void RegisterThingReferenceDefaultMetadataProvider(ThingReferenceDefaultMetadataProvider provider)
    {
        Registrations.RegisterUnique(ThingReferenceDefaultMetadataProviders, provider);
    }

    internal void RegisterThingReferenceMetadata(
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider)
    {
        string key = CompatibilityRegistrationScope.RegistrationKey(appender)
            + "|"
            + CompatibilityRegistrationScope.RegistrationKey(thingMatcher)
            + "|"
            + CompatibilityRegistrationScope.RegistrationKey(dtoMatcher)
            + "|"
            + CompatibilityRegistrationScope.RegistrationKey(applier)
            + "|"
            + CompatibilityRegistrationScope.RegistrationKey(strictnessProvider);
        RegisterThingReferenceMetadata(
            key,
            appender,
            thingMatcher,
            dtoMatcher,
            applier,
            strictnessProvider);
    }

    internal void RegisterThingReferenceMetadata(
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

        Registrations.RegisterMetadataKeyedRegistration(
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

    internal void RegisterThingReferenceDisplay(
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor)
    {
        string key = CompatibilityRegistrationScope.RegistrationKey(partsProvider) + "|" + CompatibilityRegistrationScope.RegistrationKey(standardStatSuppressor);
        RegisterThingReferenceDisplay(key, partsProvider, standardStatSuppressor);
    }

    internal void RegisterThingReferenceDisplay(
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

        Registrations.RegisterMetadataKeyedRegistration(
            ThingReferenceDisplayRegistrations,
            new ThingReferenceDisplayRegistration(key, partsProvider, standardStatSuppressor),
            nameof(ThingReferenceDisplayRegistration),
            key,
            "ThingReferenceDisplayDuplicateRejected",
            "ThingReferenceDisplayDuplicateReplaced",
            InvalidateDisplaySnapshot);
    }

    internal void RegisterThingReferenceCacheKeyPartsProvider(ThingReferenceCacheKeyPartsProvider provider)
    {
        Registrations.RegisterUnique(ThingReferenceCacheKeyPartsProviders, provider, InvalidateCacheKeySnapshot);
    }

    internal void RegisterThingReferenceMetadataNormalizer(ThingReferenceMetadataNormalizer normalizer)
    {
        Registrations.RegisterUnique(ThingReferenceMetadataNormalizers, normalizer);
    }

    internal void RegisterThingReferenceThingFactory(ThingReferenceThingFactory factory)
    {
        if (factory is null)
        {
            return;
        }

        RegisterThingReferenceThingFactory(CompatibilityRegistrationScope.RegistrationKey(factory), factory);
    }

    internal void RegisterThingReferenceThingFactory(string key, ThingReferenceThingFactory factory)
    {
        if (string.IsNullOrWhiteSpace(key) || factory is null)
        {
            return;
        }

        Registrations.RegisterMetadataKeyedRegistration(
            ThingReferenceThingFactories,
            new ThingReferenceThingFactoryRegistration(key, factory),
            nameof(ThingReferenceThingFactoryRegistration),
            key,
            "ThingReferenceThingFactoryDuplicateRejected",
            "ThingReferenceThingFactoryDuplicateReplaced");
    }

    internal bool TryMakeThingReferenceThing(ThingDef def, ThingDef? stuff, out Thing? thing)
    {
        thing = null;
        if (def is null)
        {
            return false;
        }

        foreach (ThingReferenceThingFactoryRegistration registration in ThingReferenceThingFactories.ToList())
        {
            Thing? created = null;
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal void RegisterThingReferenceDefKindPredicate(string kind, ThingReferenceDefKindPredicate predicate)
    {
        if (string.IsNullOrWhiteSpace(kind) || predicate is null)
        {
            return;
        }

        string normalizedKind = kind.Trim();
        var registration = new ThingReferenceDefKindPredicateRegistration(normalizedKind, predicate);
        Registrations.Register(ThingReferenceDefKindPredicates, registration, nameof(ThingReferenceDefKindPredicateRegistration), normalizedKind, Registrations.ActiveRegistrationPriority());
    }

    internal bool HasThingReferenceDefKind(string kind, ThingDef? def)
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

            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal void RegisterDefensePointDefNameProvider(DefensePointDefNameProvider provider)
    {
        Registrations.RegisterUnique(DefensePointDefNameProviders, provider);
    }

    internal IEnumerable<string> GetCompatibilityDefensePointDefNames()
    {
        foreach (DefensePointDefNameProvider provider in DefensePointDefNameProviders.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal bool TryDrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        foreach (ThingReferenceEditorRegistration registration in ThingReferenceEditorRegistrations.ToList())
        {
            float height = 0f;
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        foreach (ThingReferenceEditorRegistration registration in ThingReferenceEditorRegistrations.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(ClearThingReferenceMetadata),
                registration,
                () => registration.Cleaner(surface, def, item));
        }
    }

    internal void NormalizeThingReferenceMetadata(ModThingReferenceDto reference)
    {
        if (reference is null)
        {
            return;
        }

        foreach (ThingReferenceMetadataNormalizer normalizer in ThingReferenceMetadataNormalizers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(NormalizeThingReferenceMetadata),
                normalizer,
                () => normalizer(reference));
        }
    }

    internal bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        foreach (ThingReferenceEditorRegistration registration in ThingReferenceEditorRegistrations.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal void ApplyThingReferenceDefaultMetadata(string surface, ThingDef? def, ModThingReferenceDto item)
    {
        if (item is null)
        {
            return;
        }

        foreach (ThingReferenceDefaultMetadataProvider provider in ThingReferenceDefaultMetadataProviders.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(ApplyThingReferenceDefaultMetadata),
                provider,
                () => provider(surface, def, item));
        }
    }

    internal bool IsThingReferenceUniqueRequest(ThingDef? def)
    {
        foreach (ThingReferenceUniqueRequestPredicate predicate in ThingReferenceUniqueRequestPredicates.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(AppendThingReferenceMetadata),
                registration,
                () => registration.Appender(metadataThing, reference));
        }
    }

    internal bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, Thing metadataThing)
    {
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            string? localMissingDefName = null;
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal int ThingReferenceMetadataStrictness(ModThingReferenceDto requirement)
    {
        int strictness = 0;
        foreach (ThingReferenceMetadataRegistration registration in ThingReferenceMetadataRegistrations.ToList())
        {
            if (Registrations.TryInvokeCompatibilityCallback(
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

    internal void AppendThingReferenceDisplayParts(ModThingReferenceDto thing, bool asRequirement, List<string> parts)
    {
        if (parts is null)
        {
            return;
        }

        foreach (ThingReferenceDisplayRegistration registration in ThingReferenceDisplayRegistrationSnapshot())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(AppendThingReferenceDisplayParts),
                registration,
                () => registration.PartsProvider(thing, asRequirement, parts));
        }
    }

    internal bool SuppressesStandardThingStats(ThingDef? def)
    {
        foreach (ThingReferenceDisplayRegistration registration in ThingReferenceDisplayRegistrationSnapshot())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal IEnumerable<string> ThingReferenceCacheKeyParts(ModThingReferenceDto thing)
    {
        if (thing is null)
        {
            yield break;
        }

        foreach (ThingReferenceCacheKeyPartsProvider provider in ThingReferenceCacheKeyPartsProviderSnapshot())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    private IReadOnlyList<ThingReferenceDisplayRegistration> ThingReferenceDisplayRegistrationSnapshot() =>
        cachedThingReferenceDisplayRegistrations ??= ThingReferenceDisplayRegistrations.ToArray();

    private IReadOnlyList<ThingReferenceCacheKeyPartsProvider> ThingReferenceCacheKeyPartsProviderSnapshot() =>
        cachedThingReferenceCacheKeyPartsProviders ??= ThingReferenceCacheKeyPartsProviders.ToArray();

    private void InvalidateDisplaySnapshot() => cachedThingReferenceDisplayRegistrations = null;
    private void InvalidateCacheKeySnapshot() => cachedThingReferenceCacheKeyPartsProviders = null;
}
