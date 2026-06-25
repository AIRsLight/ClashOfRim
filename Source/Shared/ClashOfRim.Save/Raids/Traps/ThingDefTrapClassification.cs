namespace AIRsLight.ClashOfRim.Save;

public sealed record ThingDefTrapClassification(
    string DefName,
    string? ThingClass,
    string? SourceModId,
    bool InheritsBuildingTrap,
    bool ApprovedCustomTrap)
{
    public bool IsTrap => InheritsBuildingTrap || ApprovedCustomTrap;
}
