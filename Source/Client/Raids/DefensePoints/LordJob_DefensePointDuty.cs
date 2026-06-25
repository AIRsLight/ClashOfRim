using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class LordJob_DefensePointDuty : LordJob
{
    private IntVec3 point;
    private string dutyDefName = string.Empty;
    private float radius;
    private float? wanderRadius;

    public LordJob_DefensePointDuty()
    {
    }

    public LordJob_DefensePointDuty(IntVec3 point, string dutyDefName, float radius, float? wanderRadius)
    {
        this.point = point;
        this.dutyDefName = dutyDefName;
        this.radius = radius;
        this.wanderRadius = wanderRadius;
    }

    public override StateGraph CreateGraph()
    {
        StateGraph graph = new();
        graph.AddToil(new LordToil_DefensePointDuty(point, dutyDefName, radius, wanderRadius));
        return graph;
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref point, "point");
        string? savedDutyDefName = dutyDefName;
        Scribe_Values.Look(ref savedDutyDefName, "dutyDefName");
        dutyDefName = savedDutyDefName ?? string.Empty;
        Scribe_Values.Look(ref radius, "radius");
        Scribe_Values.Look(ref wanderRadius, "wanderRadius");
    }
}

public sealed class LordToil_DefensePointDuty : LordToil
{
    private IntVec3 point;
    private string dutyDefName = string.Empty;
    private float radius;
    private float? wanderRadius;

    public LordToil_DefensePointDuty()
    {
    }

    public LordToil_DefensePointDuty(IntVec3 point, string dutyDefName, float radius, float? wanderRadius)
    {
        this.point = point;
        this.dutyDefName = dutyDefName;
        this.radius = radius;
        this.wanderRadius = wanderRadius;
    }

    public override IntVec3 FlagLoc => point;

    public override void UpdateAllDuties()
    {
        DutyDef dutyDef = DefDatabase<DutyDef>.GetNamed(dutyDefName, errorOnFail: false) ?? DutyDefOf.Defend;
        foreach (Pawn pawn in lord.ownedPawns)
        {
            if (pawn?.mindState is null)
            {
                continue;
            }

            pawn.mindState.duty = new PawnDuty(dutyDef, point, radius);
            pawn.mindState.duty.focusSecond = point;
            pawn.mindState.duty.radius = radius;
            pawn.mindState.duty.wanderRadius = wanderRadius;
            pawn.mindState.duty.locomotion = LocomotionUrgency.Jog;
        }
    }
}
