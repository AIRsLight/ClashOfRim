using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnExchangePlacementService
{
    public static IntVec3 SpawnAtMapEdge(Pawn pawn, Map map)
    {
        IntVec3 cell = FindMapEdgeLandingCell(map);
        GenSpawn.Spawn(pawn, cell, map);
        return cell;
    }

    public static IntVec3 SpawnNearMapEdge(Pawn pawn, Map map, IntVec3 root, int radius)
    {
        IntVec3 cell = FindNearbyLandingCell(map, root, radius);
        GenSpawn.Spawn(pawn, cell, map);
        return cell;
    }

    public static Caravan CreatePlayerCaravan(Pawn pawn, int tile)
    {
        return CaravanMaker.MakeCaravan(
            new List<Pawn> { pawn },
            Faction.OfPlayer,
            tile,
            addToWorldPawnsIfNotAlready: true);
    }

    public static void AddToCaravan(Caravan caravan, Pawn pawn)
    {
        caravan.AddPawn(pawn, addCarriedPawnToWorldPawnsIfAny: true);
    }

    public static IntVec3 FindMapEdgeLandingCell(Map map)
    {
        bool Validator(IntVec3 cell)
        {
            return cell.Standable(map)
                && !cell.Fogged(map)
                && cell.GetRoom(map)?.TouchesMapEdge == true;
        }

        if (CellFinder.TryFindRandomEdgeCellWith(Validator, map, 0.5f, out IntVec3 result))
        {
            return result;
        }

        if (CellFinder.TryFindRandomEdgeCellWith(cell => cell.Standable(map) && !cell.Fogged(map), map, 0.5f, out result))
        {
            return result;
        }

        return CellFinder.RandomCell(map);
    }

    private static IntVec3 FindNearbyLandingCell(Map map, IntVec3 root, int radius)
    {
        bool Validator(IntVec3 cell)
        {
            return cell.InBounds(map)
                && cell.Standable(map)
                && !cell.Fogged(map)
                && cell.GetFirstPawn(map) is null;
        }

        if (root.IsValid && Validator(root))
        {
            return root;
        }

        if (root.IsValid && CellFinder.TryRandomClosewalkCellNear(root, map, radius, out IntVec3 result, Validator))
        {
            return result;
        }

        return FindMapEdgeLandingCell(map);
    }
}
