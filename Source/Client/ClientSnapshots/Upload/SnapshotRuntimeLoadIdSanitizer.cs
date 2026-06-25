using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal static class SnapshotRuntimeLoadIdSanitizer
{
    public static int NormalizeHediffLoadIdsForCurrentGame()
    {
        if (Current.Game is null || Find.UniqueIDsManager is null)
        {
            return 0;
        }

        var used = new HashSet<int>();
        var changed = 0;
        foreach (Pawn pawn in EnumerateCurrentGamePawns())
        {
            List<Hediff>? hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs is null)
            {
                continue;
            }

            foreach (Hediff hediff in hediffs.Where(hediff => hediff is not null))
            {
                int current = hediff.loadID;
                if (current > 0 && used.Add(current))
                {
                    continue;
                }

                int next = NextUnusedHediffId(used);
                hediff.loadID = next;
                changed++;
            }
        }

        if (changed > 0)
        {
            ClashLog.Message("[ClashOfRim][SnapshotSave] Normalized runtime hediff load IDs before saving: " + changed + ".");
        }

        return changed;
    }

    private static int NextUnusedHediffId(ISet<int> used)
    {
        int guard = 0;
        while (true)
        {
            int candidate = Find.UniqueIDsManager.GetNextHediffID();
            if (candidate > 0 && used.Add(candidate))
            {
                return candidate;
            }

            guard++;
            if (guard > 1000000)
            {
                throw new InvalidOperationException("Unable to allocate a unique hediff load ID before snapshot save.");
            }
        }
    }

    private static IEnumerable<Pawn> EnumerateCurrentGamePawns()
    {
        var seen = new HashSet<Pawn>();
        var heldThings = new List<Thing>();

        if (Current.Game?.Maps is not null)
        {
            foreach (Map map in Current.Game.Maps.Where(map => map is not null))
            {
                foreach (Pawn pawn in map.mapPawns?.AllPawns ?? Enumerable.Empty<Pawn>())
                {
                    if (pawn is not null && seen.Add(pawn))
                    {
                        yield return pawn;
                    }
                }

                heldThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(map, heldThings, allowUnreal: true);
                foreach (Pawn pawn in heldThings.OfType<Pawn>())
                {
                    if (seen.Add(pawn))
                    {
                        yield return pawn;
                    }
                }
            }
        }

        if (Current.Game?.World is not null)
        {
            heldThings.Clear();
            ThingOwnerUtility.GetAllThingsRecursively(Current.Game.World, heldThings, allowUnreal: true);
            foreach (Pawn pawn in heldThings.OfType<Pawn>())
            {
                if (seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }

        if (Find.WorldPawns is not null)
        {
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }
    }
}
