using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class JobGiver_ClashManTurretsNearDutyPoint : JobGiver_ManTurrets
{
    private const float FallbackRadius = 25f;

    protected override Job TryGiveJob(Pawn pawn)
    {
        float originalRadius = maxDistFromPoint;
        maxDistFromPoint = ResolveDutyRadius(pawn);
        try
        {
            return base.TryGiveJob(pawn);
        }
        finally
        {
            maxDistFromPoint = originalRadius;
        }
    }

    protected override IntVec3 GetRoot(Pawn pawn)
    {
        Lord? lord = pawn.GetLord();
        if (lord?.CurLordToil is not null)
        {
            return lord.CurLordToil.FlagLoc;
        }

        LocalTargetInfo focus = pawn.mindState?.duty?.focus ?? LocalTargetInfo.Invalid;
        return focus.IsValid ? focus.Cell : pawn.Position;
    }

    private static float ResolveDutyRadius(Pawn pawn)
    {
        float radius = pawn.mindState?.duty?.radius ?? FallbackRadius;
        return radius >= 0f ? radius : FallbackRadius;
    }
}
