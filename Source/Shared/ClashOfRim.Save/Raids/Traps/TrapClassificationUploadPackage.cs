namespace AIRsLight.ClashOfRim.Save;

public sealed record TrapClassificationUploadPackage(
    string FormatVersion,
    DateTimeOffset GeneratedAtUtc,
    string? AdminUserId,
    IReadOnlyList<TrapClassificationUploadEntry> Entries)
{
    public const string CurrentFormatVersion = "trap-classification-upload-v1";

    public IReadOnlyList<TrapClassificationUploadEntry> AutoApprovedEntries =>
        Entries.Where(entry => entry.InheritsBuildingTrap).ToList();

    public IReadOnlyList<TrapClassificationUploadEntry> CandidateEntries =>
        Entries.Where(entry => entry.CandidateRequiresApproval).ToList();

    public IReadOnlyList<TrapClassificationUploadEntry> ApprovedManifestEntries =>
        Entries.Where(entry => entry.IncludedInApprovedManifest).ToList();
}
