using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Traits;

public sealed class CompProperties_TraitTrainerUsable : CompProperties_Usable
{
    public CompProperties_TraitTrainerUsable()
    {
        compClass = typeof(CompTraitTrainerUsable);
    }
}

public sealed class CompTraitTrainerUsable : CompUsable
{
    protected override string FloatMenuOptionLabel(Pawn pawn)
    {
        CompUseEffectAddTrait? traitEffect = parent.TryGetComp<CompUseEffectAddTrait>();
        if (TraitTrainerUtility.IsRandomTraitTrainerDef(parent.def))
        {
            return ClashOfRimText.Key("ClashOfRim.TraitTrainer.UseRandomLabel");
        }

        string trait = TraitTrainerUtility.TraitLabel(traitEffect?.traitDefName, traitEffect?.traitDegree);
        return ClashOfRimText.Key("ClashOfRim.TraitTrainer.UseLabel", trait.Named("TRAIT"));
    }
}
