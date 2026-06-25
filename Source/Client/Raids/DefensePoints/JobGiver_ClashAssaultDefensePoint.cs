using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class JobGiver_ClashAssaultDefensePoint : JobGiver_AIFightEnemy
{
    public JobGiver_ClashAssaultDefensePoint()
    {
        targetAcquireRadius = 999999f;
        targetKeepRadius = 999999f;
        chaseTarget = true;
    }

    protected override Thing? FindAttackTarget(Pawn pawn)
    {
        if (pawn?.Map?.attackTargetsCache is null)
        {
            return null;
        }

        Thing? best = null;
        float bestDistance = float.MaxValue;
        List<IAttackTarget> targets = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
        for (int i = 0; i < targets.Count; i++)
        {
            IAttackTarget attackTarget = targets[i];
            Thing thing = attackTarget.Thing;
            if (thing is null
                || thing.Destroyed
                || !thing.Spawned
                || !thing.HostileTo(pawn)
                || attackTarget.ThreatDisabled(pawn)
                || !AttackTargetFinder.IsAutoTargetable(attackTarget)
                || !ExtraTargetValidator(pawn, thing))
            {
                continue;
            }

            float distance = thing.Position.DistanceToSquared(pawn.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = thing;
            }
        }

        return best;
    }

    protected override bool TryFindShootingPosition(Pawn pawn, out IntVec3 dest, Verb? verbToUse = null)
    {
        Thing? target = pawn.mindState?.enemyTarget;
        Verb? verb = verbToUse ?? pawn.TryGetAttackVerb(target, !pawn.IsColonist && !pawn.IsColonySubhuman, allowTurrets: false);
        if (target is null || verb is null)
        {
            dest = IntVec3.Invalid;
            return false;
        }

        return CastPositionFinder.TryFindCastPosition(
            new CastPositionRequest
            {
                caster = pawn,
                target = target,
                verb = verb,
                maxRangeFromTarget = verb.EffectiveRange,
                wantCoverFromTarget = verb.EffectiveRange > 5f
            },
            out dest);
    }
}
