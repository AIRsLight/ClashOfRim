using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionPawnStateSanitizer
{
    public static RemoteSessionPawnStateSanitizerResult DropLoadedCarriedThings(Map map)
    {
        if (map?.mapPawns is null)
        {
            return default;
        }

        int checkedPawns = 0;
        int interruptedJobs = 0;
        int droppedThings = 0;
        int destroyedThings = 0;
        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
        {
            if (pawn is null || pawn.Destroyed || pawn.Dead || pawn.carryTracker is null)
            {
                continue;
            }

            checkedPawns++;
            Thing? carried = SafeCarriedThing(pawn);
            if (carried is null || carried.Destroyed)
            {
                continue;
            }

            if (pawn.CurJob is not null)
            {
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: true);
                pawn.jobs?.ClearQueuedJobs();
                pawn.pather?.StopDead();
                interruptedJobs++;
            }

            IntVec3 dropCell = pawn.PositionHeld.IsValid ? pawn.PositionHeld : pawn.Position;
            try
            {
                if (pawn.carryTracker.TryDropCarriedThing(dropCell, ThingPlaceMode.Near, out _, null))
                {
                    droppedThings++;
                    continue;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteMap] Failed to drop carried thing from loaded pawn "
                    + pawn.LabelShort
                    + ": "
                    + ex);
            }

            carried = SafeCarriedThing(pawn);
            if (carried is not null && !carried.Destroyed)
            {
                carried.Destroy(DestroyMode.Vanish);
                destroyedThings++;
            }
        }

        return new RemoteSessionPawnStateSanitizerResult(checkedPawns, interruptedJobs, droppedThings, destroyedThings);
    }

    private static Thing? SafeCarriedThing(Pawn pawn)
    {
        try
        {
            return pawn.carryTracker?.CarriedThing;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][RemoteMap] Failed to inspect loaded pawn carried thing: pawn="
                + pawn.LabelShort
                + ", error="
                + ex.Message);
            return null;
        }
    }
}

public readonly struct RemoteSessionPawnStateSanitizerResult
{
    public RemoteSessionPawnStateSanitizerResult(
        int checkedPawns,
        int interruptedJobs,
        int droppedThings,
        int destroyedThings)
    {
        CheckedPawns = checkedPawns;
        InterruptedJobs = interruptedJobs;
        DroppedThings = droppedThings;
        DestroyedThings = destroyedThings;
    }

    public int CheckedPawns { get; }

    public int InterruptedJobs { get; }

    public int DroppedThings { get; }

    public int DestroyedThings { get; }
}
