using RimWorld;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class CompProperties_AssignableToPawn_DefensePoint : CompProperties_AssignableToPawn
{
    public CompProperties_AssignableToPawn_DefensePoint()
    {
        compClass = typeof(CompAssignableToPawn_DefensePoint);
        maxAssignedPawnsCount = 1;
    }
}
