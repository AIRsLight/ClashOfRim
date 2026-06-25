using Verse;
using Verse.AI.Group;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class LordJob_OperateDefenseEquipment : LordJob
{
    private IntVec3 point;
    private float radius = 24f;

    public LordJob_OperateDefenseEquipment()
    {
    }

    public LordJob_OperateDefenseEquipment(IntVec3 point, float radius = 24f)
    {
        this.point = point;
        this.radius = radius;
    }

    public override StateGraph CreateGraph()
    {
        var graph = new StateGraph();
        graph.AddToil(new LordToil_OperateDefenseEquipment(point, radius));
        return graph;
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref point, "point");
        Scribe_Values.Look(ref radius, "radius", 24f);
    }
}
