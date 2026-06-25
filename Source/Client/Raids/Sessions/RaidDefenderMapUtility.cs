using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidDefenderMapUtility
{
    public static RaidDefenderMapConversionResult ConvertPlayerOwnedMapObjectsToDefenderProxy(Map map, Faction defenderFaction)
    {
        int convertedPawns = 0;
        int convertedThings = 0;
        int repairableBuildingsRefreshed = 0;
        foreach (Pawn pawn in map.mapPawns?.AllPawnsSpawned?.ToList() ?? new List<Pawn>())
        {
            if (pawn.Faction == Faction.OfPlayer)
            {
                pawn.SetFaction(defenderFaction);
                convertedPawns++;
            }
        }

        foreach (Thing thing in map.listerThings?.AllThings?.ToList() ?? new List<Thing>())
        {
            if (thing.Faction == Faction.OfPlayer)
            {
                thing.SetFaction(defenderFaction);
                convertedThings++;
                if (thing is Building)
                {
                    repairableBuildingsRefreshed++;
                }
                continue;
            }

            if (thing is Building building && ShouldAssignDefenderFaction(building, defenderFaction))
            {
                building.SetFaction(defenderFaction);
                convertedThings++;
                repairableBuildingsRefreshed++;
            }
        }

        return new RaidDefenderMapConversionResult(convertedPawns, convertedThings, repairableBuildingsRefreshed);
    }

    private static bool ShouldAssignDefenderFaction(Building building, Faction defenderFaction)
    {
        if (building.Destroyed || !building.Spawned)
        {
            return false;
        }

        if (building.Faction == defenderFaction)
        {
            RefreshRepairableBuildingRegistration(building);
            return false;
        }

        if (building.Faction is not null)
        {
            return false;
        }

        ThingDef? def = building.def;
        if (def?.building is null)
        {
            return false;
        }

        return !def.building.isNaturalRock
            && !def.IsBlueprint
            && !def.IsFrame;
    }

    private static void RefreshRepairableBuildingRegistration(Building building)
    {
        if (!building.Spawned)
        {
            return;
        }

        building.Map?.listerBuildingsRepairable?.Notify_BuildingDeSpawned(building);
        building.Map?.listerBuildingsRepairable?.Notify_BuildingSpawned(building);
    }
}

public readonly struct RaidDefenderMapConversionResult
{
    public RaidDefenderMapConversionResult(
        int convertedPawns,
        int convertedThings,
        int repairableBuildingsRefreshed)
    {
        ConvertedPawns = convertedPawns;
        ConvertedThings = convertedThings;
        RepairableBuildingsRefreshed = repairableBuildingsRefreshed;
    }

    public int ConvertedPawns { get; }

    public int ConvertedThings { get; }

    public int RepairableBuildingsRefreshed { get; }
}
