using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class JobGiver_ClashReturnToDefensePoint : ThinkNode_JobGiver
{
    protected override Job? TryGiveJob(Pawn pawn)
    {
        PawnDuty? duty = pawn.mindState?.duty;
        Map? map = pawn.Map;
        if (duty is null || map is null || !duty.focus.IsValid)
        {
            return null;
        }

        float radius = Math.Max(1f, duty.wanderRadius ?? duty.radius);
        IntVec3 center = duty.focus.Cell;
        if (pawn.Position.InHorDistOf(center, radius))
        {
            return null;
        }

        IntVec3 destination = RCellFinder.RandomWanderDestFor(
            pawn,
            center,
            radius,
            null,
            Danger.Deadly,
            canBashDoors: false);
        if (!destination.IsValid)
        {
            return null;
        }

        Job job = JobMaker.MakeJob(JobDefOf.Goto, destination);
        job.locomotionUrgency = PawnUtility.ResolveLocomotion(pawn, LocomotionUrgency.Jog);
        job.expiryInterval = 600;
        job.checkOverrideOnExpire = true;
        return job;
    }
}
