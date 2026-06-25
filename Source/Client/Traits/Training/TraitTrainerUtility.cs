using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Traits;

internal static class TraitTrainerUtility
{
    public const string ThingDefName = "ClashOfRim_TraitTrainer";
    public const string RandomThingDefName = "ClashOfRim_RandomTraitTrainer";
    public const string MetadataTraitDefName = "clashofrim.traitTrainer.traitDefName";
    public const string MetadataTraitDegree = "clashofrim.traitTrainer.traitDegree";
    private static List<TraitSelection>? cachedTraitSelections;

    public static bool IsTraitTrainerDef(ThingDef? def)
    {
        return string.Equals(def?.defName, ThingDefName, StringComparison.Ordinal);
    }

    public static bool IsRandomTraitTrainerDef(ThingDef? def)
    {
        return string.Equals(def?.defName, RandomThingDefName, StringComparison.Ordinal);
    }

    public static bool IsAnyTraitTrainerDef(ThingDef? def)
    {
        return IsTraitTrainerDef(def) || IsRandomTraitTrainerDef(def);
    }

    public static bool IsTraitTrainerReference(ModThingReferenceDto? reference)
    {
        return string.Equals(reference?.DefName, ThingDefName, StringComparison.Ordinal);
    }

    public static bool IsRandomTraitTrainerReference(ModThingReferenceDto? reference)
    {
        return string.Equals(reference?.DefName, RandomThingDefName, StringComparison.Ordinal);
    }

    public static TraitDef? ResolveTraitDef(string? defName)
    {
        return string.IsNullOrWhiteSpace(defName)
            ? null
            : DefDatabase<TraitDef>.GetNamedSilentFail(defName);
    }

    public static int? TraitDegree(ModThingReferenceDto reference)
    {
        if (reference.Metadata is not null
            && reference.Metadata.TryGetValue(MetadataTraitDegree, out string? degreeText)
            && int.TryParse(degreeText, out int degree))
        {
            return degree;
        }

        return null;
    }

    public static string? TraitDefName(ModThingReferenceDto reference)
    {
        if (reference.Metadata is not null
            && reference.Metadata.TryGetValue(MetadataTraitDefName, out string? traitDefName)
            && !string.IsNullOrWhiteSpace(traitDefName))
        {
            return traitDefName;
        }

        return null;
    }

    public static string TraitLabel(string? traitDefName, int? degree)
    {
        TraitDef? traitDef = ResolveTraitDef(traitDefName);
        if (traitDef is null)
        {
            return string.IsNullOrWhiteSpace(traitDefName)
                ? ClashOfRimText.Key("ClashOfRim.TraitTrainer.NoTrait")
                : traitDefName!;
        }

        TraitDegreeData? data = DegreeData(traitDef, degree);
        return data?.LabelCap ?? traitDef.LabelCap;
    }

    public static string TraitLabel(ModThingReferenceDto reference)
    {
        return TraitLabel(TraitDefName(reference), TraitDegree(reference));
    }

    public static string ThingLabel(Thing thing)
    {
        if (IsRandomTraitTrainerDef(thing.def))
        {
            return ClashOfRimText.Key(
                "ClashOfRim.TraitTrainer.ItemLabel",
                ClashOfRimText.Key("ClashOfRim.TraitTrainer.Random").Named("TRAIT"));
        }

        CompUseEffectAddTrait? traitEffect = thing.TryGetComp<CompUseEffectAddTrait>();
        string trait = TraitLabel(traitEffect?.traitDefName, traitEffect?.traitDegree);
        return ClashOfRimText.Key("ClashOfRim.TraitTrainer.ItemLabel", trait.Named("TRAIT"));
    }

    public static string ThingDescription(Thing thing)
    {
        if (IsRandomTraitTrainerDef(thing.def))
        {
            return ClashOfRimText.Key("ClashOfRim.TraitTrainer.RandomDescription");
        }

        return ClashOfRimText.Key("ClashOfRim.TraitTrainer.Description");
    }

    public static void SetTrait(ModThingReferenceDto reference, TraitDef traitDef, int degree)
    {
        reference.Metadata ??= new Dictionary<string, string?>(StringComparer.Ordinal);
        reference.Metadata[MetadataTraitDefName] = traitDef.defName;
        reference.Metadata[MetadataTraitDegree] = degree.ToString();
    }

    public static void ClearTrait(ModThingReferenceDto reference)
    {
        reference.Metadata?.Remove(MetadataTraitDefName);
        reference.Metadata?.Remove(MetadataTraitDegree);
    }

    public static IEnumerable<TraitSelection> AllTraitSelections()
    {
        return cachedTraitSelections ??= DefDatabase<TraitDef>.AllDefsListForReading
            .Where(def => !def.degreeDatas.NullOrEmpty())
            .SelectMany(def => def.degreeDatas.Select(data => new TraitSelection(def, data.degree)))
            .OrderBy(selection => TraitLabel(selection.Def.defName, selection.Degree))
            .ThenBy(selection => selection.Def.defName, StringComparer.Ordinal)
            .ThenBy(selection => selection.Degree)
            .ToList();
    }

    public static TraitDegreeData? DegreeData(TraitDef traitDef, int? degree)
    {
        if (traitDef.degreeDatas.NullOrEmpty())
        {
            return null;
        }

        if (degree.HasValue)
        {
            TraitDegreeData? exact = traitDef.degreeDatas.FirstOrDefault(data => data.degree == degree.Value);
            if (exact is not null)
            {
                return exact;
            }
        }

        return traitDef.degreeDatas.FirstOrDefault(data => data.degree == 0)
            ?? traitDef.degreeDatas.FirstOrDefault();
    }

    public static IEnumerable<TraitSelection> RandomTraitSelections()
    {
        return AllTraitSelections();
    }

    public static IEnumerable<TraitSelection> AvailableRandomTraitSelections(Pawn pawn)
    {
        if (pawn.story?.traits is null)
        {
            return Enumerable.Empty<TraitSelection>();
        }

        return RandomTraitSelections()
            .Where(selection => !pawn.story.traits.allTraits.Any(trait => trait.def == selection.Def))
            .Where(selection => !pawn.story.traits.allTraits.Any(trait => trait.def.ConflictsWith(selection.Def)));
    }
}

internal readonly struct TraitSelection
{
    public TraitSelection(TraitDef def, int degree)
    {
        Def = def;
        Degree = degree;
    }

    public TraitDef Def { get; }

    public int Degree { get; }
}
