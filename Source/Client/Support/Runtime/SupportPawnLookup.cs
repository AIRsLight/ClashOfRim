using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal static class SupportPawnLookup
{
    internal static Pawn? Find(string thingId) => string.IsNullOrWhiteSpace(thingId) ? null
        : PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.FirstOrDefault(p => string.Equals(p.ThingID, thingId, StringComparison.Ordinal)) ?? FindActive(thingId);
    internal static Pawn? FindActive(string thingId)
    {
        if (string.IsNullOrWhiteSpace(thingId)) return null;
        foreach (Map map in Verse.Find.Maps ?? new List<Map>())
        {
            var pawn = map.mapPawns?.AllPawnsSpawned.FirstOrDefault(p => string.Equals(p.ThingID, thingId, StringComparison.Ordinal));
            if (pawn is not null) return pawn;
        }
        return null;
    }
}
