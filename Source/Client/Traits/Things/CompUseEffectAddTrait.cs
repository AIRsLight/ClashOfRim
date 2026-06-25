using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Traits;

public sealed class CompProperties_UseEffectAddTrait : CompProperties_UseEffect
{
    public bool randomTrait;

    public CompProperties_UseEffectAddTrait()
    {
        compClass = typeof(CompUseEffectAddTrait);
    }
}

public sealed class CompUseEffectAddTrait : CompUseEffect
{
    public string? traitDefName;
    public int traitDegree;

    private CompProperties_UseEffectAddTrait Props => (CompProperties_UseEffectAddTrait)props;

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_Values.Look(ref traitDefName, "clashOfRimTraitDefName");
        Scribe_Values.Look(ref traitDegree, "clashOfRimTraitDegree");
    }

    public override void PostPostMake()
    {
        base.PostPostMake();
        EnsureRandomTraitAssigned();
    }

    public override void DoEffect(Pawn usedBy)
    {
        base.DoEffect(usedBy);
        TraitSelection? randomFallback = Props.randomTrait ? RandomTraitSelectionFor(usedBy) : null;
        TraitDef? traitDef = Props.randomTrait
            ? randomFallback?.Def
            : TraitTrainerUtility.ResolveTraitDef(traitDefName);
        TraitDegreeData? degreeData = traitDef is null
            ? null
            : TraitTrainerUtility.DegreeData(traitDef, Props.randomTrait ? randomFallback?.Degree : traitDegree);
        if (traitDef is null || degreeData is null || usedBy.story?.traits is null)
        {
            if (Props.randomTrait)
            {
                return;
            }

            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.TraitTrainer.Invalid"),
                usedBy,
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        Trait? existingSameDef = usedBy.story.traits.allTraits
            .FirstOrDefault(trait => trait.def == traitDef);
        if (existingSameDef is not null)
        {
            usedBy.story.traits.RemoveTrait(existingSameDef);
        }

        var traitToAdd = new Trait(traitDef, degreeData.degree, forced: false);
        usedBy.story.traits.GainTrait(traitToAdd);
        TraitUtility.ApplySkillGainFromTrait(usedBy, traitToAdd);
        Messages.Message(
            ClashOfRimText.Key(
                "ClashOfRim.TraitTrainer.Used",
                usedBy.LabelShort.Named("PAWN"),
                TraitTrainerUtility.TraitLabel(traitDef.defName, degreeData.degree).Named("TRAIT")),
            usedBy,
            MessageTypeDefOf.PositiveEvent,
            historical: true);
    }

    public override AcceptanceReport CanBeUsedBy(Pawn pawn)
    {
        AcceptanceReport baseReport = base.CanBeUsedBy(pawn);
        if (!baseReport.Accepted)
        {
            return baseReport;
        }

        if (pawn.story?.traits is null)
        {
            if (Props.randomTrait)
            {
                return true;
            }

            return ClashOfRimText.Key("ClashOfRim.TraitTrainer.NoStory");
        }

        if (Props.randomTrait)
        {
            return true;
        }

        TraitDef? traitDef = TraitTrainerUtility.ResolveTraitDef(traitDefName);
        TraitDegreeData? degreeData = traitDef is null
            ? null
            : TraitTrainerUtility.DegreeData(traitDef, traitDegree);
        if (traitDef is null || degreeData is null)
        {
            return ClashOfRimText.Key("ClashOfRim.TraitTrainer.Invalid");
        }

        if (pawn.story.traits.HasTrait(traitDef, degreeData.degree))
        {
            return ClashOfRimText.Key(
                "ClashOfRim.TraitTrainer.AlreadyHasTrait",
                TraitTrainerUtility.TraitLabel(traitDef.defName, degreeData.degree).Named("TRAIT"));
        }

        Trait? conflicting = pawn.story.traits.allTraits
            .FirstOrDefault(trait => trait.def != traitDef && trait.def.ConflictsWith(traitDef));
        if (conflicting is not null)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.TraitTrainer.ConflictingTrait",
                conflicting.LabelCap.Named("TRAIT"));
        }

        return true;
    }

    public override TaggedString ConfirmMessage(Pawn pawn)
    {
        if (Props.randomTrait)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.TraitTrainer.ConfirmUseRandom",
                pawn.LabelShort.Named("PAWN"));
        }

        TraitDef? traitDef = TraitTrainerUtility.ResolveTraitDef(traitDefName);
        TraitDegreeData? degreeData = traitDef is null
            ? null
            : TraitTrainerUtility.DegreeData(traitDef, traitDegree);
        if (traitDef is null || degreeData is null)
        {
            return TaggedString.Empty;
        }

        return ClashOfRimText.Key(
            "ClashOfRim.TraitTrainer.ConfirmUse",
            pawn.LabelShort.Named("PAWN"),
            TraitTrainerUtility.TraitLabel(traitDef.defName, degreeData.degree).Named("TRAIT"));
    }

    private void EnsureRandomTraitAssigned()
    {
        if (!Props.randomTrait || !string.IsNullOrWhiteSpace(traitDefName))
        {
            return;
        }

        TraitSelection? selection = RandomTraitSelection();
        if (selection is null)
        {
            return;
        }

        traitDefName = selection.Value.Def.defName;
        traitDegree = selection.Value.Degree;
    }

    private static TraitSelection? RandomTraitSelection()
    {
        List<TraitSelection> candidates = TraitTrainerUtility.RandomTraitSelections().ToList();
        return candidates.Count == 0 ? null : candidates.RandomElement();
    }

    private TraitSelection? RandomTraitSelectionFor(Pawn pawn)
    {
        EnsureRandomTraitAssigned();
        TraitDef? assignedTrait = TraitTrainerUtility.ResolveTraitDef(traitDefName);
        TraitDegreeData? assignedDegreeData = assignedTrait is null
            ? null
            : TraitTrainerUtility.DegreeData(assignedTrait, traitDegree);
        if (assignedTrait is not null && assignedDegreeData is not null && CanGainTrait(pawn, assignedTrait))
        {
            return new TraitSelection(assignedTrait, assignedDegreeData.degree);
        }

        List<TraitSelection> candidates = TraitTrainerUtility.AvailableRandomTraitSelections(pawn).ToList();
        return candidates.Count == 0 ? null : candidates.RandomElement();
    }

    private static bool CanGainTrait(Pawn pawn, TraitDef traitDef)
    {
        return pawn.story?.traits is not null
            && !pawn.story.traits.allTraits.Any(trait => trait.def == traitDef)
            && !pawn.story.traits.allTraits.Any(trait => trait.def.ConflictsWith(traitDef));
    }
}
