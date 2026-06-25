using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Admin;

public static class TrapClassificationUploadBuilder
{
    public static TrapClassificationUploadPackage Build(
        IEnumerable<TrapClassificationScanEntry> scanEntries,
        IEnumerable<string>? adminApprovedCandidateDefNames,
        DateTimeOffset generatedAtUtc,
        string? adminUserId)
    {
        if (scanEntries == null)
        {
            throw new ArgumentNullException(nameof(scanEntries));
        }

        var approvedCandidates = new HashSet<string>(
            adminApprovedCandidateDefNames ?? Array.Empty<string>(),
            StringComparer.Ordinal);

        IReadOnlyList<TrapClassificationUploadEntry> entries = scanEntries
            .Select(entry => new TrapClassificationUploadEntry(
                entry.DefName,
                entry.ThingClass,
                entry.ModPackageId,
                entry.ModName,
                entry.Status,
                entry.Reason,
                entry.Status == TrapClassificationScanStatus.CandidateRequiresApproval &&
                    approvedCandidates.Contains(entry.DefName)))
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();

        return new TrapClassificationUploadPackage(
            TrapClassificationUploadPackage.CurrentFormatVersion,
            generatedAtUtc,
            adminUserId,
            entries);
    }
}
