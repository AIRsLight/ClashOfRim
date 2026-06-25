using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidAttackerLossCaravanMatcher
{
    public static RaidAttackerLossCaravanMatch? FindMatchingCaravan(IEnumerable<string>? lostPawnGlobalKeys)
    {
        List<HashSet<string>> requiredKeyGroups = BuildLocalPawnKeyGroups(lostPawnGlobalKeys);
        if (requiredKeyGroups.Count == 0 || Find.WorldObjects == null)
        {
            return null;
        }

        HashSet<string> localKeys = new(requiredKeyGroups.SelectMany(group => group), StringComparer.Ordinal);
        List<Caravan> caravans = Find.WorldObjects.Caravans;
        for (int i = 0; i < caravans.Count; i++)
        {
            Caravan caravan = caravans[i];
            if (caravan == null || caravan.Destroyed || !caravan.Spawned || !caravan.IsPlayerControlled)
            {
                continue;
            }

            RaidAttackerLossCaravanMatch? match = TryMatch(caravan, localKeys, requiredKeyGroups);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static RaidAttackerLossCaravanMatch? TryMatch(
        Caravan caravan,
        HashSet<string> localKeys,
        List<HashSet<string>> requiredKeyGroups)
    {
        List<Pawn> pawns = caravan.PawnsListForReading;
        if (pawns == null || pawns.Count == 0)
        {
            return null;
        }

        List<Pawn> matchedPawns = new();
        List<Pawn> ownerPawns = new();
        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            if (pawn == null || pawn.Destroyed)
            {
                continue;
            }

            if (caravan.IsOwner(pawn))
            {
                ownerPawns.Add(pawn);
            }

            if (MatchesPawn(pawn, localKeys))
            {
                matchedPawns.Add(pawn);
            }
        }

        if (matchedPawns.Count == 0)
        {
            return null;
        }

        bool coversEveryOwnerPawn = ownerPawns.Count > 0 && ownerPawns.All(pawn => MatchesPawn(pawn, localKeys));
        bool containsEveryLostPawn = requiredKeyGroups.All(group => matchedPawns.Any(pawn => MatchesAnyPawnKey(pawn, group)));

        return new RaidAttackerLossCaravanMatch(
            caravan,
            matchedPawns,
            ownerPawns,
            coversEveryOwnerPawn && containsEveryLostPawn);
    }

    private static bool MatchesPawn(Pawn pawn, HashSet<string> localKeys)
    {
        return localKeys.Contains(pawn.ThingID) ||
            localKeys.Contains(pawn.GetUniqueLoadID());
    }

    private static bool MatchesAnyPawnKey(Pawn pawn, HashSet<string> keys)
    {
        return keys.Contains(pawn.ThingID) || keys.Contains(pawn.GetUniqueLoadID());
    }

    private static List<HashSet<string>> BuildLocalPawnKeyGroups(IEnumerable<string>? globalKeys)
    {
        List<HashSet<string>> groups = new();
        if (globalKeys == null)
        {
            return groups;
        }

        foreach (string globalKey in globalKeys)
        {
            if (string.IsNullOrWhiteSpace(globalKey))
            {
                continue;
            }

            HashSet<string> keys = new(StringComparer.Ordinal);
            AddKey(keys, globalKey);
            int thingMarker = globalKey.LastIndexOf("/thing:", StringComparison.Ordinal);
            if (thingMarker >= 0)
            {
                AddKey(keys, globalKey.Substring(thingMarker + "/thing:".Length));
            }
            else
            {
                int looseMarker = globalKey.LastIndexOf("thing:", StringComparison.Ordinal);
                if (looseMarker >= 0)
                {
                    AddKey(keys, globalKey.Substring(looseMarker + "thing:".Length));
                }
            }

            groups.Add(keys);
        }

        return groups;
    }

    private static void AddKey(HashSet<string> keys, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        keys.Add(key);
        if (key.StartsWith("Thing_", StringComparison.Ordinal))
        {
            keys.Add(key.Substring("Thing_".Length));
        }
        else
        {
            keys.Add("Thing_" + key);
        }
    }
}
