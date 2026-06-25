namespace AIRsLight.ClashOfRim.Admin;

public sealed class TrapClassificationUploadEntry
{
    public TrapClassificationUploadEntry(
        string defName,
        string? thingClass,
        string? modPackageId,
        string? modName,
        TrapClassificationScanStatus scanStatus,
        string scanReason,
        bool adminApproved)
    {
        DefName = defName;
        ThingClass = thingClass;
        ModPackageId = modPackageId;
        ModName = modName;
        ScanStatus = scanStatus;
        ScanReason = scanReason;
        AdminApproved = adminApproved;
    }

    public string DefName { get; }

    public string? ThingClass { get; }

    public string? ModPackageId { get; }

    public string? ModName { get; }

    public TrapClassificationScanStatus ScanStatus { get; }

    public string ScanReason { get; }

    public bool AdminApproved { get; }

    public bool IncludedInApprovedManifest =>
        ScanStatus == TrapClassificationScanStatus.ApprovedByInheritance || AdminApproved;
}
