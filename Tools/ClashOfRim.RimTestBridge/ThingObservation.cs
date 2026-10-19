using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

// Independent game facts shared by transfer and catalog observations.
internal static class ThingObservation
{
    internal static object Read(Thing t)
    {
        var value = t is MinifiedThing m ? m.InnerThing : t;
        if (value is null) return new { defName = t.def.defName, emptyMinified = true };
        var coded = value.TryGetComp<CompBiocodable>();
        return new
        {
            defName = t.def.defName, innerDef = t is MinifiedThing ? value.def.defName : null, count = t.stackCount,
            stuff = value.Stuff?.defName, quality = value.TryGetComp<CompQuality>()?.Quality.ToString(),
            hp = value.def.useHitPoints ? (int?)value.HitPoints : null, tainted = (value as Apparel)?.WornByCorpse,
            deterioration = AmbientDamageObservation.Loss(value),
            biocoded = coded?.Biocoded, codedPawn = coded?.Biocoded == true ? coded.CodedPawnLabel : null,
            ingredients = value.TryGetComp<CompIngredients>()?.ingredients.Select(d => d.defName).Order(StringComparer.Ordinal).ToArray(),
            genes = (value as GeneSetHolderBase)?.GeneSet?.GenesListForReading.Select(g => g.defName).Order(StringComparer.Ordinal).ToArray(),
            color = value.TryGetComp<CompColorable>()?.Color.ToString(),
            style = value.StyleDef?.defName,
            rotTicks = value.TryGetComp<CompRottable>()?.RotProgress,
            fuel = value.TryGetComp<CompRefuelable>()?.Fuel
        };
    }
}
