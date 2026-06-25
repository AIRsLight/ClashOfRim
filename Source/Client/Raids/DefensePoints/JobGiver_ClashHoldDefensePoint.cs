using RimWorld;
using Verse;
using Verse.AI;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class JobGiver_ClashHoldDefensePoint : JobGiver_AIFightEnemy
{
    protected override bool TryFindShootingPosition(Pawn pawn, out IntVec3 dest, Verb? verbToUse = null)
    {
        dest = IntVec3.Invalid;
        Thing? target = pawn.mindState?.enemyTarget;
        Verb? verb = verbToUse ?? pawn.TryGetAttackVerb(target, !pawn.IsColonist && !pawn.IsColonySubhuman, allowTurrets: false);
        if (target is null || verb is null || !verb.CanHitTarget(target))
        {
            return false;
        }

        dest = pawn.Position;
        return true;
    }

    protected override Job? MeleeAttackJob(Pawn pawn, Thing enemyTarget)
    {
        return pawn.CanReachImmediate(enemyTarget, PathEndMode.Touch)
            ? base.MeleeAttackJob(pawn, enemyTarget)
            : null;
    }
}
