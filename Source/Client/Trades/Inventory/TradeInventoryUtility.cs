using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal static class TradeInventoryUtility
{
    public static IEnumerable<Thing> AccessibleMapItems(Map map, bool beaconOnly)
    {
        if (beaconOnly)
        {
            foreach (Thing thing in BeaconRangeItems(map))
            {
                if (TradeThingReferenceUtility.IsTradeableItem(thing))
                {
                    yield return thing;
                }
            }

            yield break;
        }

        var seen = new HashSet<Thing>();
        foreach (Thing thing in BeaconRangeItems(map))
        {
            if (seen.Add(thing) && TradeThingReferenceUtility.IsTradeableItem(thing))
            {
                yield return thing;
            }
        }

        foreach (Thing thing in StorageItems(map))
        {
            if (seen.Add(thing) && TradeThingReferenceUtility.IsTradeableItem(thing))
            {
                yield return thing;
            }
        }
    }

    public static IEnumerable<Thing> AccessibleMapThings(Map map, bool beaconOnly)
    {
        foreach (Thing thing in AccessibleMapItems(map, beaconOnly))
        {
            yield return thing;
        }

        foreach (Pawn pawn in TradePawnUtility.TradeableMapPawns(map))
        {
            yield return pawn;
        }
    }

    private static IEnumerable<Thing> BeaconRangeItems(Map map)
    {
        foreach (Thing thing in TradeUtility.AllLaunchableThingsForTrade(map, null))
        {
            if (thing is not null
                && !thing.Destroyed
                && thing.Spawned
                && thing.Map == map)
            {
                yield return thing;
            }
        }
    }

    private static IEnumerable<Thing> StorageItems(Map map)
    {
        if (map.haulDestinationManager is null)
        {
            yield break;
        }

        foreach (SlotGroup group in map.haulDestinationManager.AllGroupsListForReading)
        {
            if (group?.parent?.HaulDestinationEnabled != true)
            {
                continue;
            }

            foreach (Thing thing in group.HeldThings)
            {
                if (thing is not null
                    && !thing.Destroyed
                    && thing.Spawned
                    && thing.Map == map)
                {
                    yield return thing;
                }
            }
        }
    }
}
