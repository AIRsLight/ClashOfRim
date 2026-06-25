using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class LordToil_OperateDefenseEquipment : LordToil
{
    private const string OperateDefenseEquipmentDutyDefName = "ClashOfRim_OperateDefenseEquipment";
    private readonly IntVec3 point;
    private readonly float radius;

    public LordToil_OperateDefenseEquipment(IntVec3 point, float radius)
    {
        this.point = point;
        this.radius = radius;
    }

    public override IntVec3 FlagLoc => point;

    public override void UpdateAllDuties()
    {
        DutyDef dutyDef = DefDatabase<DutyDef>.GetNamed(OperateDefenseEquipmentDutyDefName, errorOnFail: false)
            ?? DutyDefOf.ManClosestTurret;

        foreach (Pawn pawn in lord.ownedPawns)
        {
            if (pawn?.mindState is null)
            {
                continue;
            }

            pawn.mindState.duty = new PawnDuty(dutyDef, point, radius);
            pawn.mindState.duty.focusSecond = point;
            pawn.mindState.duty.locomotion = LocomotionUrgency.Jog;
        }
    }
}
