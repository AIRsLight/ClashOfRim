namespace AIRsLight.ClashOfRim.Save;

public sealed record TrapClassificationUploadEntry(
    string DefName,
    string? ThingClass,
    string? SourceModId,
    string? SourceModName,
    bool InheritsBuildingTrap,
    bool CandidateRequiresApproval,
    string ScanReason,
    bool AdminApproved)
{
    public bool IncludedInApprovedManifest => InheritsBuildingTrap || AdminApproved;
}
