using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Raids;

public static class DefensePointRaidAiApplicator
{
    private const float HoldRadius = 0f;
    private const string HoldDutyDefName = "ClashOfRim_HoldDefensePoint";
    private const string DefendDutyDefName = "ClashOfRim_DefendDefensePoint";
    private const string AssaultDutyDefName = "ClashOfRim_AssaultDefensePoint";

    public static void Apply(Map map, Faction defenderFaction)
    {
        List<Building_ClashDefensePoint> points = DefensePointUtility.AllDefensePoints(map).ToList();
        List<Pawn> assigned = new();
        HashSet<Pawn> assignedSet = new();
        var assignedByMode = new Dictionary<DefensePointAiMode, int>();
        int pointLordCount = 0;

        foreach (Building_ClashDefensePoint point in points)
        {
            CompAssignableToPawn? comp = point.GetComp<CompAssignableToPawn>();
            if (comp is null)
            {
                continue;
            }

            List<Pawn> pawns = comp.AssignedPawnsForReading
                .Where(pawn => IsUsableDefender(pawn, defenderFaction))
                .Distinct()
                .ToList();
            if (pawns.Count == 0)
            {
                continue;
            }

            foreach (Pawn pawn in pawns)
            {
                if (assignedSet.Add(pawn))
                {
                    assigned.Add(pawn);
                }
            }

            PreparePawnsForDefenseAi(pawns);
            Lord lord = MakeLordForPoint(map, defenderFaction, point, pawns);
            RefreshLordDuties(lord);
            pointLordCount++;
            assignedByMode[point.AiMode] = assignedByMode.TryGetValue(point.AiMode, out int current)
                ? current + pawns.Count
                : pawns.Count;
        }

        List<Pawn> unassigned = map.mapPawns.PawnsInFaction(defenderFaction)
            .Where(pawn => IsUsableDefender(pawn, defenderFaction))
            .Where(pawn => !assignedSet.Contains(pawn))
            .ToList();
        if (unassigned.Count > 0)
        {
            IntVec3 fallback = points.FirstOrDefault()?.Position ?? map.Center;
            PreparePawnsForDefenseAi(unassigned);
            RemoveExistingLords(unassigned);
            Lord fallbackLord = LordMaker.MakeNewLord(
                defenderFaction,
                new LordJob_DefensePointDuty(
                    fallback,
                    DefendDutyDefName,
                    Building_ClashDefensePoint.DefaultDefendRadius,
                    Building_ClashDefensePoint.DefaultActionRadius),
                map,
                unassigned);
            RefreshLordDuties(fallbackLord);
        }

        int pawnsWithLord = 0;
        int pawnsWithDuty = 0;
        foreach (Pawn pawn in assigned)
        {
            CountPawnLordState(pawn, ref pawnsWithLord, ref pawnsWithDuty);
        }

        foreach (Pawn pawn in unassigned)
        {
            CountPawnLordState(pawn, ref pawnsWithLord, ref pawnsWithDuty);
        }

        ClashLog.Message(
            "[ClashOfRim][DefensePoint] Applied defense AI: points="
            + points.Count
            + ", assignedPawns="
            + assigned.Count
            + ", fallbackPawns="
            + unassigned.Count
            + ", pointLords="
            + pointLordCount
            + ", pawnsWithLord="
            + pawnsWithLord
            + ", pawnsWithDuty="
            + pawnsWithDuty
            + ", modes="
            + FormatModeCounts(assignedByMode));
    }

    private static void CountPawnLordState(Pawn pawn, ref int pawnsWithLord, ref int pawnsWithDuty)
    {
        if (pawn.GetLord() is not null)
        {
            pawnsWithLord++;
        }

        if (pawn.mindState?.duty is not null)
        {
            pawnsWithDuty++;
        }
    }

    private static bool IsUsableDefender(Pawn pawn, Faction defenderFaction)
    {
        return pawn is { Spawned: true, Dead: false, Downed: false } &&
            pawn.Faction == defenderFaction &&
            pawn.mindState is not null;
    }

    private static Lord MakeLordForPoint(
        Map map,
        Faction defenderFaction,
        Building_ClashDefensePoint point,
        List<Pawn> pawns)
    {
        RemoveExistingLords(pawns);
        LordJob lordJob = point.AiMode switch
        {
            DefensePointAiMode.Hold => new LordJob_DefensePointDuty(point.Position, HoldDutyDefName, HoldRadius, null),
            DefensePointAiMode.Assault => new LordJob_DefensePointDuty(
                point.Position,
                AssaultDutyDefName,
                System.Math.Max(Building_ClashDefensePoint.DefaultDefendRadius, point.ActionRadius),
                point.ActionRadius),
            DefensePointAiMode.OperateEquipment => new LordJob_OperateDefenseEquipment(point.Position, point.ActionRadius),
            _ => new LordJob_DefensePointDuty(
                point.Position,
                DefendDutyDefName,
                System.Math.Max(Building_ClashDefensePoint.DefaultDefendRadius, point.ActionRadius),
                point.ActionRadius)
        };

        return LordMaker.MakeNewLord(defenderFaction, lordJob, map, pawns);
    }

    private static void RefreshLordDuties(Lord lord)
    {
        lord.CurLordToil?.UpdateAllDuties();
    }

    private static string FormatModeCounts(Dictionary<DefensePointAiMode, int> assignedByMode)
    {
        if (assignedByMode.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ",",
            assignedByMode
                .OrderBy(pair => pair.Key.ToString())
                .Select(pair => pair.Key + ":" + pair.Value));
    }

    private static void RemoveExistingLords(IEnumerable<Pawn> pawns)
    {
        foreach (Pawn pawn in pawns)
        {
            Lord? existing = pawn.GetLord();
            existing?.RemovePawn(pawn);
        }
    }

    private static void PreparePawnsForDefenseAi(IEnumerable<Pawn> pawns)
    {
        foreach (Pawn pawn in pawns)
        {
            if (pawn is null || pawn.Destroyed || pawn.Dead || !pawn.Spawned)
            {
                continue;
            }

            RestUtility.WakeUp(pawn, true);
            if (pawn.CurJob is not null)
            {
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: true, canReturnToPool: true);
            }

            pawn.mindState.enemyTarget = null;
            pawn.mindState.meleeThreat = null;
        }
    }
}
