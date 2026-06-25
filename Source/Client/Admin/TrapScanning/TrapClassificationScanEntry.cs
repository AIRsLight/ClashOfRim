namespace AIRsLight.ClashOfRim.Admin;

public sealed class TrapClassificationScanEntry
{
    public TrapClassificationScanEntry(
        string defName,
        string? thingClass,
        string? modPackageId,
        string? modName,
        TrapClassificationScanStatus status,
        string reason)
    {
        DefName = defName;
        ThingClass = thingClass;
        ModPackageId = modPackageId;
        ModName = modName;
        Status = status;
        Reason = reason;
    }

    public string DefName { get; }

    public string? ThingClass { get; }

    public string? ModPackageId { get; }

    public string? ModName { get; }

    public TrapClassificationScanStatus Status { get; }

    public string Reason { get; }

    public bool IsAutoApproved => Status == TrapClassificationScanStatus.ApprovedByInheritance;
}
