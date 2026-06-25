using Verse;

namespace AIRsLight.ClashOfRim.Traits;

public sealed class Thing_TraitTrainer : ThingWithComps
{
    public override string LabelNoCount => TraitTrainerUtility.ThingLabel(this).CapitalizeFirst(def);

    public override string DescriptionDetailed => TraitTrainerUtility.ThingDescription(this);
}
