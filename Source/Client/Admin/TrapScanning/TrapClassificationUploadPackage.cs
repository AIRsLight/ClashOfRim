using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Admin;

public sealed class TrapClassificationUploadPackage
{
    public const string CurrentFormatVersion = "trap-classification-upload-v1";

    public TrapClassificationUploadPackage(
        string formatVersion,
        DateTimeOffset generatedAtUtc,
        string? adminUserId,
        IReadOnlyList<TrapClassificationUploadEntry> entries)
    {
        FormatVersion = formatVersion;
        GeneratedAtUtc = generatedAtUtc;
        AdminUserId = adminUserId;
        Entries = entries;
    }

    public string FormatVersion { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public string? AdminUserId { get; }

    public IReadOnlyList<TrapClassificationUploadEntry> Entries { get; }

    public IReadOnlyList<TrapClassificationUploadEntry> AutoApprovedEntries =>
        Entries.Where(entry => entry.ScanStatus == TrapClassificationScanStatus.ApprovedByInheritance).ToList();

    public IReadOnlyList<TrapClassificationUploadEntry> CandidateEntries =>
        Entries.Where(entry => entry.ScanStatus == TrapClassificationScanStatus.CandidateRequiresApproval).ToList();

    public IReadOnlyList<TrapClassificationUploadEntry> ApprovedManifestEntries =>
        Entries.Where(entry => entry.IncludedInApprovedManifest).ToList();
}
