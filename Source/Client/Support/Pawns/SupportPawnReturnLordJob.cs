using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Support;

public sealed class SupportPawnReturnLordJob : LordJob_ExitMapBest
{
    public SupportPawnReturnLordJob()
    {
    }

    public SupportPawnReturnLordJob(LocomotionUrgency locomotion, bool canDig, bool canDefendSelf)
        : base(locomotion, canDig, canDefendSelf)
    {
    }

    public override bool ShouldRemovePawn(Pawn p, PawnLostCondition reason)
    {
        return reason != PawnLostCondition.Incapped;
    }
}
