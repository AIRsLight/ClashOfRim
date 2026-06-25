using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class PlaceWorker_DefensePoint : PlaceWorker
{
    public override AcceptanceReport AllowsPlacing(
        BuildableDef checkingDef,
        IntVec3 loc,
        Rot4 rot,
        Map map,
        Thing? thingToIgnore = null,
        Thing? thing = null)
    {
        foreach (Thing existing in loc.GetThingList(map))
        {
            if (existing == thingToIgnore)
            {
                continue;
            }

            ThingDef? placedDef = ResolvePlacedThingDef(existing);
            if (placedDef is null)
            {
                continue;
            }

            if (DefensePointUtility.IsDefensePointDef(placedDef.defName))
            {
                return "ClashOfRim.DefensePoint.PlaceBlockedDefensePoint".Translate();
            }

            if (placedDef.category == ThingCategory.Building)
            {
                return "ClashOfRim.DefensePoint.PlaceBlockedBuilding".Translate();
            }
        }

        return AcceptanceReport.WasAccepted;
    }

    private static ThingDef? ResolvePlacedThingDef(Thing thing)
    {
        if (thing.def.entityDefToBuild is ThingDef entityThingDef)
        {
            return entityThingDef;
        }

        return thing.def;
    }
}
