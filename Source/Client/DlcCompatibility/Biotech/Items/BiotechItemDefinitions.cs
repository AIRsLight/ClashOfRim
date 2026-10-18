using RimWorld;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;

internal static class BiotechItemDefinitions
{
    internal static bool IsGeneSetHolder(ThingDef? def)
    {
        return def?.thingClass is not null
            && typeof(GeneSetHolderBase).IsAssignableFrom(def.thingClass);
    }

    internal static bool HasPawnSources(ThingDef? def)
    {
        return def?.comps is not null
            && def.comps.Any(props => props?.compClass is not null
                && typeof(CompHasPawnSources).IsAssignableFrom(props.compClass));
    }
}
