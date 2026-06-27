using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CoreCompatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradeThingReferenceUtility
{
    public const string UniqueWeaponNameMetadataKey = "ClashOfRim.UniqueWeaponName";
    public const string UniqueWeaponColorDefMetadataKey = "ClashOfRim.UniqueWeaponColorDef";
    public const string BladelinkWeaponLastKillTickMetadataKey = "ClashOfRim.BladelinkLastKillTick";
    public const string WeaponTraitKindMetadataKey = "ClashOfRim.WeaponTraitKind";
    public const string WeaponTraitKindSpecialized = "Specialized";
    public const string WeaponTraitKindPersona = "Persona";

    private static readonly FieldInfo? BiocodedField = typeof(CompBiocodable).GetField(
        "biocoded",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? BiocodedPawnLabelField = typeof(CompBiocodable).GetField(
        "codedPawnLabel",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? BiocodedPawnField = typeof(CompBiocodable).GetField(
        "codedPawn",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? UniqueWeaponNameField = typeof(CompUniqueWeapon).GetField(
        "name",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? UniqueWeaponColorField = typeof(CompUniqueWeapon).GetField(
        "color",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? UniqueWeaponIgnoreAccuracyCacheField = typeof(CompUniqueWeapon).GetField(
        "ignoreAccuracyMaluses",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? BladelinkWeaponLastKillTickField = typeof(CompBladelinkWeapon).GetField(
        "lastKillTick",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly IReadOnlyDictionary<string, int> QualityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Awful"] = 0,
            ["Poor"] = 1,
            ["Normal"] = 2,
            ["Good"] = 3,
            ["Excellent"] = 4,
            ["Masterwork"] = 5,
            ["Legendary"] = 6
        };
    private static readonly Dictionary<string, IReadOnlyList<ThingDef>> AllowedStuffDefsCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> StuffLabelCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsTradeableItem(Thing thing)
    {
        return thing.def?.category == ThingCategory.Item || TryGetMinifiedInnerThing(thing, out _);
    }

    public static bool IsTradeableItemDef(ThingDef def)
    {
        return def.category == ThingCategory.Item
            || (def.building?.isNaturalRock != true && def.Minifiable);
    }

    public static ModThingReferenceDto BuildThingReference(
        Thing thing,
        string globalKey,
        int count,
        string? biocodedPawnGlobalId)
    {
        Thing metadataThing = ThingForMetadata(thing);
        QualityCategory quality;
        string? qualityValue = QualityUtility.TryGetQuality(metadataThing, out quality)
            ? quality.ToString()
            : null;
        Apparel? apparel = metadataThing as Apparel;
        CompBiocodable? biocodable = metadataThing.TryGetComp<CompBiocodable>();
        bool biocoded = biocodable?.Biocoded == true;
        bool traitWeapon = IsWeaponWithTraits(metadataThing);

        var reference = new ModThingReferenceDto
        {
            GlobalKey = globalKey,
            DefName = thing.def.defName,
            StackCount = Math.Max(1, count),
            Quality = qualityValue,
            HitPoints = metadataThing.def.useHitPoints ? metadataThing.HitPoints : null,
            StuffDefName = metadataThing.Stuff?.defName,
            MaxHitPoints = metadataThing.def.useHitPoints ? metadataThing.MaxHitPoints : null,
            WornByCorpse = apparel?.WornByCorpse,
            Biocoded = biocoded ? true : null,
            BiocodedPawnLabel = biocoded ? biocodable?.CodedPawnLabel : null,
            BiocodedPawnGlobalId = biocoded ? biocodedPawnGlobalId : null,
            DisplayLabel = thing.LabelCapNoCount,
            MarketValue = thing.MarketValue,
            UniqueWeapon = traitWeapon ? true : null,
            UniqueWeaponTraits = WeaponTraitDefNames(metadataThing)
        };
        Dictionary<string, string?> metadataBeforeExplicitCompatibility = SnapshotMetadata(reference);
        AppendWeaponTraitMetadata(metadataThing, reference);
        ClashOfRimCompatibilityApi.AppendThingReferenceMetadata(metadataThing, reference);
        if (!MetadataChanged(metadataBeforeExplicitCompatibility, reference.Metadata))
        {
            ThingStatePackageUtility.TryAttachFallbackPackage(metadataThing, reference);
        }

        if (TryGetMinifiedInnerThing(thing, out Thing? innerThing))
        {
            reference.MinifiedInnerDefName = innerThing.def.defName;
            reference.MinifiedInnerStuffDefName = innerThing.Stuff?.defName;
            reference.MinifiedInnerQuality = qualityValue;
            reference.MinifiedInnerHitPoints = innerThing.def.useHitPoints ? innerThing.HitPoints : null;
            reference.MinifiedInnerMaxHitPoints = innerThing.def.useHitPoints ? innerThing.MaxHitPoints : null;
            reference.StackCount = 1;
        }

        return reference;
    }

    private static Dictionary<string, string?> SnapshotMetadata(ModThingReferenceDto reference)
    {
        return reference.Metadata is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(reference.Metadata, StringComparer.Ordinal);
    }

    private static bool MetadataChanged(
        IReadOnlyDictionary<string, string?> previous,
        IReadOnlyDictionary<string, string?>? current)
    {
        if (current is null)
        {
            return previous.Count > 0;
        }

        if (previous.Count != current.Count)
        {
            return true;
        }

        foreach (KeyValuePair<string, string?> entry in previous)
        {
            if (!current.TryGetValue(entry.Key, out string? value)
                || !string.Equals(entry.Value, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static ThingDef? ResolveReferenceDef(ModThingReferenceDto reference)
    {
        return TradeUiUtility.ResolveThingDef(reference.MinifiedInnerDefName)
            ?? TradeUiUtility.ResolveThingDef(reference.DefName);
    }

    public static bool TryMakeThing(ModThingReferenceDto reference, int stackCount, out Thing? thing, out string? missingDefName)
    {
        thing = null;
        missingDefName = null;
        if (reference.ThingPackage is not null
            && ThingStatePackageUtility.TryRestore(reference, stackCount, out thing, out missingDefName)
            && thing is not null)
        {
            ApplyThingMetadata(thing, reference, reference.Quality, reference.HitPoints, reference.WornByCorpse);
            if (!ClashOfRimCompatibilityApi.TryApplyThingReferenceMetadata(reference, thing, out missingDefName))
            {
                thing = null;
                return false;
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(reference.MinifiedInnerDefName))
        {
            ThingDef? innerDef = TradeUiUtility.ResolveThingDef(reference.MinifiedInnerDefName);
            if (innerDef is null)
            {
                missingDefName = reference.MinifiedInnerDefName;
                return false;
            }

            ThingDef? stuff = ResolveConcreteStuff(innerDef, reference.MinifiedInnerStuffDefName);
            Thing innerThing = ThingMaker.MakeThing(innerDef, stuff);
            ApplyThingMetadata(
                innerThing,
                reference,
                reference.MinifiedInnerQuality ?? reference.Quality,
                reference.MinifiedInnerHitPoints ?? reference.HitPoints,
                reference.WornByCorpse);
            if (!ClashOfRimCompatibilityApi.TryApplyThingReferenceMetadata(reference, innerThing, out missingDefName))
            {
                return false;
            }

            MinifiedThing? minifiedThing = innerThing.MakeMinified();
            if (minifiedThing is null)
            {
                missingDefName = innerDef.defName;
                return false;
            }

            minifiedThing.stackCount = 1;
            thing = minifiedThing;
            return true;
        }

        ThingDef? def = TradeUiUtility.ResolveThingDef(reference.DefName);
        if (def is null)
        {
            missingDefName = reference.DefName;
            return false;
        }

        ThingDef? outerStuff = ResolveConcreteStuff(def, reference.StuffDefName);
        if (!ClashOfRimCompatibilityApi.TryMakeThingReferenceThing(def, outerStuff, out thing))
        {
            thing = ThingMaker.MakeThing(def, outerStuff);
        }

        if (thing is null)
        {
            missingDefName = def.defName;
            return false;
        }

        thing.stackCount = Math.Max(1, stackCount);
        ApplyThingMetadata(thing, reference, reference.Quality, reference.HitPoints, reference.WornByCorpse);
        if (!ClashOfRimCompatibilityApi.TryApplyThingReferenceMetadata(reference, thing, out missingDefName))
        {
            thing = null;
            return false;
        }

        return true;
    }

    public static bool MatchesRequirement(
        ModThingReferenceDto requirement,
        ModThingReferenceDto candidate,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null)
    {
        if (!ReferenceDefsMatch(requirement.DefName, candidate.DefName))
        {
            return false;
        }

        if (!ReferenceDefsMatch(requirement.MinifiedInnerDefName, candidate.MinifiedInnerDefName))
        {
            return false;
        }

        if (!QualityMatches(candidate.MinifiedInnerQuality ?? candidate.Quality, requirement.MinifiedInnerQuality ?? requirement.Quality, qualityRequirementMode))
        {
            return false;
        }

        if (!StuffMatches(requirement.MinifiedInnerStuffDefName ?? requirement.StuffDefName, candidate.MinifiedInnerStuffDefName ?? candidate.StuffDefName))
        {
            return false;
        }

        if (!UniqueWeaponMatches(requirement, candidate))
        {
            return false;
        }

        if (!ClashOfRimCompatibilityApi.ThingReferenceMetadataMatches(requirement, candidate))
        {
            return false;
        }

        return HitPointsMatches(
            EffectiveHitPoints(candidate),
            EffectiveMaxHitPoints(candidate),
            EffectiveHitPoints(requirement),
            EffectiveMaxHitPoints(requirement),
            hitPointsRequirementMode);
    }

    public static bool MatchesRequirement(
        ModThingReferenceDto requirement,
        Thing candidate,
        string? qualityRequirementMode = null,
        string? hitPointsRequirementMode = null)
    {
        if (!ReferenceDefsMatch(requirement.DefName, candidate.def.defName))
        {
            return false;
        }

        Thing metadataThing = ThingForMetadata(candidate);
        if (!ReferenceDefsMatch(requirement.MinifiedInnerDefName, metadataThing.def.defName))
        {
            return false;
        }

        if (candidate is Pawn)
        {
            return ClashOfRimCompatibilityApi.ThingReferenceMetadataMatches(requirement, candidate);
        }

        QualityCategory quality;
        string? candidateQuality = QualityUtility.TryGetQuality(metadataThing, out quality)
            ? quality.ToString()
            : null;
        if (!QualityMatches(candidateQuality, requirement.MinifiedInnerQuality ?? requirement.Quality, qualityRequirementMode))
        {
            return false;
        }

        if (!StuffMatches(requirement.MinifiedInnerStuffDefName ?? requirement.StuffDefName, metadataThing.Stuff?.defName))
        {
            return false;
        }

        if (!UniqueWeaponMatches(requirement, metadataThing))
        {
            return false;
        }

        if (!ClashOfRimCompatibilityApi.ThingReferenceMetadataMatches(requirement, metadataThing))
        {
            return false;
        }

        return HitPointsMatches(
            metadataThing.def.useHitPoints ? metadataThing.HitPoints : null,
            metadataThing.def.useHitPoints ? metadataThing.MaxHitPoints : null,
            EffectiveHitPoints(requirement),
            EffectiveMaxHitPoints(requirement),
            hitPointsRequirementMode);
    }

    public static int RequirementStrictness(ModThingReferenceDto requirement)
    {
        string? requiredQuality = requirement.MinifiedInnerQuality ?? requirement.Quality;
        int qualityRank = !string.IsNullOrWhiteSpace(requiredQuality)
            && QualityRank.TryGetValue(requiredQuality!, out int rank)
                ? rank
                : -1;
        int hitPoints = DisplayedHitPointsPercentOrRaw(EffectiveHitPoints(requirement), EffectiveMaxHitPoints(requirement)) ?? -1;
        int traitWeaponRank = requirement.UniqueWeapon == true
            ? 100_000 + (requirement.UniqueWeaponTraits?.Count ?? 0) * 10_000
            : 0;
        int stuffRank = string.IsNullOrWhiteSpace(requirement.MinifiedInnerStuffDefName ?? requirement.StuffDefName) ? 0 : 250_000;
        return ClashOfRimCompatibilityApi.ThingReferenceMetadataStrictness(requirement)
            + traitWeaponRank
            + stuffRank
            + qualityRank * 1000
            + hitPoints;
    }

    public static bool IsBookDef(ThingDef? def)
    {
        return ClashOfRimCompatibilityApi.HasThingReferenceDefKind(CoreThingReferenceMetadata.DefKindBook, def);
    }

    public static bool IsTechprintDef(ThingDef? def)
    {
        return ClashOfRimCompatibilityApi.HasThingReferenceDefKind(CoreThingReferenceMetadata.DefKindTechprint, def);
    }

    public static bool IsSkillBookDef(ThingDef? def)
    {
        return ClashOfRimCompatibilityApi.HasThingReferenceDefKind(CoreThingReferenceMetadata.DefKindSkillBook, def);
    }

    public static bool DefSupportsStuff(ThingDef? def)
    {
        return def?.MadeFromStuff == true;
    }

    public static bool IsWeaponWithTraits(Thing thing)
    {
        return thing.TryGetComp<CompUniqueWeapon>() is not null
            || thing.TryGetComp<CompBladelinkWeapon>() is not null;
    }

    public static bool IsWeaponWithTraitsDef(ThingDef? def)
    {
        return def is not null
            && (def.HasComp(typeof(CompUniqueWeapon)) || def.HasComp(typeof(CompBladelinkWeapon)));
    }

    public static string? WeaponTraitKind(Thing thing)
    {
        if (thing.TryGetComp<CompBladelinkWeapon>() is not null)
        {
            return WeaponTraitKindPersona;
        }

        return thing.TryGetComp<CompUniqueWeapon>() is not null
            ? WeaponTraitKindSpecialized
            : null;
    }

    public static string? WeaponTraitKind(ThingDef? def)
    {
        if (def is null)
        {
            return null;
        }

        if (def.HasComp(typeof(CompBladelinkWeapon)))
        {
            return WeaponTraitKindPersona;
        }

        return def.HasComp(typeof(CompUniqueWeapon))
            ? WeaponTraitKindSpecialized
            : null;
    }

    public static string? WeaponTraitKind(ModThingReferenceDto? reference)
    {
        if (reference is null)
        {
            return null;
        }

        if (TryReadKnownWeaponTraitKind(reference.Metadata, out string? kind))
        {
            return kind;
        }

        return WeaponTraitKind(ResolveReferenceDef(reference));
    }

    public static List<string> WeaponTraitDefNames(Thing thing)
    {
        return WeaponTraitDefs(thing)
            .Select(trait => trait.defName)
            .Where(defName => !string.IsNullOrWhiteSpace(defName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IEnumerable<WeaponTraitDef> WeaponTraitDefs(Thing thing)
    {
        CompBladelinkWeapon? bladelinkWeapon = thing.TryGetComp<CompBladelinkWeapon>();
        if (bladelinkWeapon is not null)
        {
            return bladelinkWeapon.TraitsListForReading ?? Enumerable.Empty<WeaponTraitDef>();
        }

        CompUniqueWeapon? uniqueWeapon = thing.TryGetComp<CompUniqueWeapon>();
        return uniqueWeapon?.TraitsListForReading ?? Enumerable.Empty<WeaponTraitDef>();
    }

    public static ThingDef? ResolveConcreteStuff(ThingDef def, string? stuffDefName)
    {
        ThingDef? stuff = TradeUiUtility.ResolveThingDef(stuffDefName);
        if (stuff is not null || !def.MadeFromStuff)
        {
            return stuff;
        }

        return GenStuff.DefaultStuffFor(def)
            ?? AllowedStuffDefs(def).FirstOrDefault();
    }

    public static IReadOnlyList<ThingDef> AllowedStuffDefs(ThingDef def)
    {
        if (string.IsNullOrWhiteSpace(def.defName))
        {
            return Array.Empty<ThingDef>();
        }

        if (AllowedStuffDefsCache.TryGetValue(def.defName, out IReadOnlyList<ThingDef> cached))
        {
            return cached;
        }

        IReadOnlyList<ThingDef> allowed = GenStuff.AllowedStuffsFor(def, TechLevel.Undefined, false)
            .OrderBy(stuff => stuff.label)
            .ThenBy(stuff => stuff.defName)
            .ToList();
        AllowedStuffDefsCache[def.defName] = allowed;
        return allowed;
    }

    public static string StuffLabel(string? defName)
    {
        if (string.IsNullOrWhiteSpace(defName))
        {
            return ClashOfRimText.Key("ClashOfRim.Any");
        }

        string key = defName!;
        if (StuffLabelCache.TryGetValue(key, out string cached))
        {
            return cached;
        }

        ThingDef? stuff = TradeUiUtility.ResolveThingDef(key);
        string label = stuff?.label?.CapitalizeFirst() ?? key;
        StuffLabelCache[key] = label;
        return label;
    }

    private static Thing ThingForMetadata(Thing thing)
    {
        return TryGetMinifiedInnerThing(thing, out Thing? innerThing)
            ? innerThing
            : thing;
    }

    private static bool TryGetMinifiedInnerThing(Thing thing, out Thing innerThing)
    {
        if (thing is MinifiedThing { InnerThing: not null } minifiedThing)
        {
            innerThing = minifiedThing.InnerThing;
            return true;
        }

        innerThing = null!;
        return false;
    }

    private static bool ReferenceDefsMatch(string? requirementDefName, string? candidateDefName)
    {
        return string.IsNullOrWhiteSpace(requirementDefName)
            || string.Equals(requirementDefName, candidateDefName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StuffMatches(string? requiredStuffDefName, string? candidateStuffDefName)
    {
        return string.IsNullOrWhiteSpace(requiredStuffDefName)
            || string.Equals(requiredStuffDefName, candidateStuffDefName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool QualityAtLeast(string? candidate, string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(candidate)
            && QualityRank.TryGetValue(candidate!, out int candidateRank)
            && QualityRank.TryGetValue(requirement!, out int requirementRank)
            && candidateRank >= requirementRank;
    }

    private static bool QualityAtMost(string? candidate, string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(candidate)
            && QualityRank.TryGetValue(candidate!, out int candidateRank)
            && QualityRank.TryGetValue(requirement!, out int requirementRank)
            && candidateRank <= requirementRank;
    }

    private static bool QualityMatches(string? candidate, string? requirement, string? mode)
    {
        return string.Equals(mode, "AtMost", StringComparison.Ordinal)
            ? QualityAtMost(candidate, requirement)
            : QualityAtLeast(candidate, requirement);
    }

    private static bool HitPointsMatches(int? candidate, int? candidateMax, int? requirement, int? requirementMax, string? mode)
    {
        if (!requirement.HasValue)
        {
            return true;
        }

        if (!candidate.HasValue)
        {
            return false;
        }

        int candidateComparable = DisplayedHitPointsPercentOrRaw(candidate, candidateMax) ?? candidate.Value;
        int requirementComparable = DisplayedHitPointsPercentOrRaw(requirement, requirementMax) ?? requirement.Value;
        return string.Equals(mode, "AtMost", StringComparison.Ordinal)
            ? candidateComparable <= requirementComparable
            : candidateComparable >= requirementComparable;
    }

    private static int? EffectiveHitPoints(ModThingReferenceDto reference)
    {
        return reference.MinifiedInnerHitPoints ?? reference.HitPoints;
    }

    private static int? EffectiveMaxHitPoints(ModThingReferenceDto reference)
    {
        return reference.MinifiedInnerMaxHitPoints ?? reference.MaxHitPoints;
    }

    private static int? DisplayedHitPointsPercentOrRaw(int? hitPoints, int? maxHitPoints)
    {
        if (!hitPoints.HasValue)
        {
            return null;
        }

        if (!maxHitPoints.HasValue || maxHitPoints.Value <= 0)
        {
            return hitPoints.Value;
        }

        return Mathf.Clamp(Mathf.RoundToInt(hitPoints.Value * 100f / Math.Max(1, maxHitPoints.Value)), 1, 100);
    }

    private static bool UniqueWeaponMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate)
    {
        if (requirement.UniqueWeapon != true)
        {
            return true;
        }

        return candidate.UniqueWeapon == true
            && RequiredUniqueWeaponTraitsMatch(requirement.UniqueWeaponTraits, candidate.UniqueWeaponTraits);
    }

    private static bool UniqueWeaponMatches(ModThingReferenceDto requirement, Thing candidate)
    {
        if (requirement.UniqueWeapon != true)
        {
            return true;
        }

        return IsWeaponWithTraits(candidate)
            && RequiredUniqueWeaponTraitsMatch(
                requirement.UniqueWeaponTraits,
                WeaponTraitDefNames(candidate));
    }

    private static bool RequiredUniqueWeaponTraitsMatch(IEnumerable<string>? requiredTraits, IEnumerable<string>? candidateTraits)
    {
        HashSet<string> candidate = new(candidateTraits ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return (requiredTraits ?? Array.Empty<string>())
            .Where(trait => !string.IsNullOrWhiteSpace(trait))
            .All(candidate.Contains);
    }

    private static void ApplyThingMetadata(
        Thing thing,
        ModThingReferenceDto reference,
        string? qualityValue,
        int? hitPoints,
        bool? wornByCorpse)
    {
        if (hitPoints.HasValue && thing.def.useHitPoints)
        {
            thing.HitPoints = Math.Max(1, Math.Min(thing.MaxHitPoints, hitPoints.Value));
        }

        if (!string.IsNullOrWhiteSpace(qualityValue)
            && Enum.TryParse(qualityValue, ignoreCase: true, out QualityCategory quality))
        {
            thing.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);
        }

        if (thing is Apparel apparel && wornByCorpse.HasValue)
        {
            apparel.WornByCorpse = wornByCorpse.Value;
        }

        ApplyBiocodedState(thing, reference);
        ApplyWeaponTraitState(thing, reference);
    }

    private static void AppendWeaponTraitMetadata(Thing thing, ModThingReferenceDto reference)
    {
        if (thing.TryGetComp<CompBladelinkWeapon>() is not null)
        {
            AppendBladelinkWeaponMetadata(thing, reference);
            return;
        }

        CompUniqueWeapon? uniqueWeapon = thing.TryGetComp<CompUniqueWeapon>();
        if (uniqueWeapon is null)
        {
            return;
        }

        reference.Metadata[WeaponTraitKindMetadataKey] = WeaponTraitKindSpecialized;
        if (UniqueWeaponNameField?.GetValue(uniqueWeapon) is string name && !string.IsNullOrWhiteSpace(name))
        {
            reference.Metadata[UniqueWeaponNameMetadataKey] = name;
        }

        if (UniqueWeaponColorField?.GetValue(uniqueWeapon) is ColorDef colorDef && !string.IsNullOrWhiteSpace(colorDef.defName))
        {
            reference.Metadata[UniqueWeaponColorDefMetadataKey] = colorDef.defName;
        }
    }

    private static void AppendBladelinkWeaponMetadata(Thing thing, ModThingReferenceDto reference)
    {
        CompBladelinkWeapon? bladelinkWeapon = thing.TryGetComp<CompBladelinkWeapon>();
        if (bladelinkWeapon is null)
        {
            return;
        }

        reference.Metadata[WeaponTraitKindMetadataKey] = WeaponTraitKindPersona;
        if (BladelinkWeaponLastKillTickField?.GetValue(bladelinkWeapon) is int lastKillTick)
        {
            reference.Metadata[BladelinkWeaponLastKillTickMetadataKey] = lastKillTick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static void ApplyBiocodedState(Thing thing, ModThingReferenceDto reference)
    {
        if (reference.Biocoded != true)
        {
            return;
        }

        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        if (biocodable is null)
        {
            return;
        }

        string label = string.IsNullOrWhiteSpace(reference.BiocodedPawnLabel)
            ? "Unknown".TranslateSimple()
            : reference.BiocodedPawnLabel!;
        BiocodedField?.SetValue(biocodable, true);
        BiocodedPawnLabelField?.SetValue(biocodable, label);
        BiocodedPawnField?.SetValue(biocodable, null);
    }

    private static void ApplyWeaponTraitState(Thing thing, ModThingReferenceDto reference)
    {
        if (reference.UniqueWeapon != true && (reference.UniqueWeaponTraits is null || reference.UniqueWeaponTraits.Count == 0))
        {
            return;
        }

        string? kind = WeaponTraitKind(reference);
        if (string.Equals(kind, WeaponTraitKindPersona, StringComparison.Ordinal))
        {
            CompBladelinkWeapon? bladelinkWeapon = thing.TryGetComp<CompBladelinkWeapon>();
            if (bladelinkWeapon is not null)
            {
                ApplyBladelinkWeaponState(bladelinkWeapon, reference);
            }

            return;
        }

        if (string.Equals(kind, WeaponTraitKindSpecialized, StringComparison.Ordinal))
        {
            CompUniqueWeapon? specializedWeapon = thing.TryGetComp<CompUniqueWeapon>();
            if (specializedWeapon is not null)
            {
                ApplyUniqueWeaponState(thing, specializedWeapon, reference);
            }

            return;
        }

        CompBladelinkWeapon? fallbackBladelinkWeapon = thing.TryGetComp<CompBladelinkWeapon>();
        if (fallbackBladelinkWeapon is not null)
        {
            ApplyBladelinkWeaponState(fallbackBladelinkWeapon, reference);
            return;
        }

        CompUniqueWeapon? uniqueWeapon = thing.TryGetComp<CompUniqueWeapon>();
        if (uniqueWeapon is not null)
        {
            ApplyUniqueWeaponState(thing, uniqueWeapon, reference);
        }
    }

    private static bool TryReadKnownWeaponTraitKind(
        IReadOnlyDictionary<string, string?>? metadata,
        out string? kind)
    {
        kind = null;
        if (metadata is null
            || !metadata.TryGetValue(WeaponTraitKindMetadataKey, out string? rawKind)
            || string.IsNullOrWhiteSpace(rawKind))
        {
            return false;
        }

        if (string.Equals(rawKind, WeaponTraitKindPersona, StringComparison.Ordinal))
        {
            kind = WeaponTraitKindPersona;
            return true;
        }

        if (string.Equals(rawKind, WeaponTraitKindSpecialized, StringComparison.Ordinal))
        {
            kind = WeaponTraitKindSpecialized;
            return true;
        }

        return false;
    }

    private static void ApplyUniqueWeaponState(Thing thing, CompUniqueWeapon uniqueWeapon, ModThingReferenceDto reference)
    {
        uniqueWeapon.TraitsListForReading.Clear();
        foreach (WeaponTraitDef trait in ResolveWeaponTraitDefs(reference.UniqueWeaponTraits))
        {
            if (!uniqueWeapon.TraitsListForReading.Contains(trait))
            {
                uniqueWeapon.TraitsListForReading.Add(trait);
            }
        }

        if (reference.Metadata.TryGetValue(UniqueWeaponNameMetadataKey, out string? uniqueName)
            && !string.IsNullOrWhiteSpace(uniqueName))
        {
            UniqueWeaponNameField?.SetValue(uniqueWeapon, uniqueName);
            if (thing.TryGetComp(out CompArt compArt))
            {
                compArt.Title = uniqueName;
            }
        }

        if (reference.Metadata.TryGetValue(UniqueWeaponColorDefMetadataKey, out string? colorDefName)
            && !string.IsNullOrWhiteSpace(colorDefName))
        {
            ColorDef? colorDef = DefDatabase<ColorDef>.GetNamedSilentFail(colorDefName);
            if (colorDef is not null)
            {
                UniqueWeaponColorField?.SetValue(uniqueWeapon, colorDef);
            }
        }

        UniqueWeaponIgnoreAccuracyCacheField?.SetValue(uniqueWeapon, null);
        uniqueWeapon.Setup(fromSave: false);
    }

    private static void ApplyBladelinkWeaponState(CompBladelinkWeapon bladelinkWeapon, ModThingReferenceDto reference)
    {
        bladelinkWeapon.TraitsListForReading.Clear();
        foreach (WeaponTraitDef trait in ResolveWeaponTraitDefs(reference.UniqueWeaponTraits)
                     .Where(trait => trait.weaponCategory == WeaponCategoryDefOf.BladeLink))
        {
            if (!bladelinkWeapon.TraitsListForReading.Contains(trait))
            {
                bladelinkWeapon.TraitsListForReading.Add(trait);
            }
        }

        if (reference.Metadata.TryGetValue(BladelinkWeaponLastKillTickMetadataKey, out string? lastKillTickText)
            && int.TryParse(lastKillTickText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int lastKillTick))
        {
            BladelinkWeaponLastKillTickField?.SetValue(bladelinkWeapon, lastKillTick);
        }
    }

    private static IEnumerable<WeaponTraitDef> ResolveWeaponTraitDefs(IEnumerable<string>? traitDefNames)
    {
        foreach (string traitDefName in traitDefNames ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(traitDefName))
            {
                continue;
            }

            WeaponTraitDef? trait = DefDatabase<WeaponTraitDef>.GetNamedSilentFail(traitDefName)
                ?? DefDatabase<WeaponTraitDef>.AllDefs.FirstOrDefault(def =>
                    string.Equals(def.label, traitDefName, StringComparison.OrdinalIgnoreCase));
            if (trait is not null)
            {
                yield return trait;
            }
        }
    }
}
