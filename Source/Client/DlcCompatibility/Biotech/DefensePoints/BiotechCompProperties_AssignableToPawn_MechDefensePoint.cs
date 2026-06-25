using AIRsLight.ClashOfRim.Raids;
using RimWorld;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

public sealed class CompProperties_AssignableToPawn_MechDefensePoint : CompProperties_AssignableToPawn
{
    public CompProperties_AssignableToPawn_MechDefensePoint()
    {
        compClass = typeof(CompAssignableToPawn_MechDefensePoint);
        maxAssignedPawnsCount = 1;
    }
}
