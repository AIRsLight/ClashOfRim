using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionMapThingAccessSanitizer
{
    public static int ForbidLoadedHaulables(Map? map)
    {
        if (map?.listerThings?.AllThings is null)
        {
            return 0;
        }

        int forbidden = 0;
        foreach (Thing thing in map.listerThings.AllThings.ToList())
        {
            if (!ShouldForbidLoadedThing(thing))
            {
                continue;
            }

            try
            {
                thing.SetForbidden(true, warnOnFail: false);
                forbidden++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to forbid loaded remote map thing "
                    + Describe(thing)
                    + ": "
                    + ex);
            }
        }

        if (forbidden > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteSession] Forbid loaded remote map haulables: map="
                + map.GetUniqueLoadID()
                + ", count="
                + forbidden
                + ".");
        }

        return forbidden;
    }

    private static bool ShouldForbidLoadedThing(Thing thing)
    {
        if (thing is null || thing.Destroyed || !thing.Spawned)
        {
            return false;
        }

        if (thing is Pawn or Building or Blueprint or Frame)
        {
            return false;
        }

        if (thing.def is null || !thing.def.EverHaulable)
        {
            return false;
        }

        return thing is ThingWithComps thingWithComps
            && thingWithComps.GetComp<CompForbiddable>() is not null;
    }

    private static string Describe(Thing thing)
    {
        return (thing.def?.defName ?? thing.GetType().Name)
            + "/"
            + thing.ThingID;
    }
}
