using static AIRsLight.ClashOfRim.Compatibility.CompatibilityManifestRules;

namespace AIRsLight.ClashOfRim.Compatibility;

// A detail packet is a proper subset of its accompanying client summary, not a
// manifest with fewer mods than the server. Recheck that summary against current policy.
public static class CompatibilityManifestDetailsComparer
{
    public static bool IsSelection(CompatibilityManifest details, CompatibilityManifestSummary? summary)
    {
        if (summary is null || details.Mods.Count >= summary.Mods.Count) return false;
        var declared = new HashSet<string>(summary.Mods.Select(mod => NormalizeId(mod.PackageId)), StringComparer.Ordinal);
        return details.Mods.All(mod => declared.Contains(NormalizeId(mod.PackageId)));
    }

    public static CompatibilityComparisonResult Compare(CompatibilityManifest server, CompatibilityManifest details,
        CompatibilityManifestSummary? summary, CompatibilityComparisonOptions options)
    {
        if (!IsSelection(details, summary)) return CompatibilityManifestComparer.Compare(server, details, options);
        if (details.ManifestId != summary!.ManifestId)
            return Reject("Detail packet and summary identify different client manifests.");
        var advertised = CompatibilityManifestSummaryComparer.Compare(server, summary, options);
        if (advertised.Issues.Any(issue => issue.Severity == CompatibilityIssueSeverity.Error)) return new(advertised.Issues);
        if (advertised.RequestedPackageIds.Contains("*"))
            return Reject("A full manifest is required to validate additional mods.");
        var provided = new HashSet<string>(details.Mods.Select(mod => NormalizeId(mod.PackageId)), StringComparer.Ordinal);
        if (advertised.RequestedPackageIds.Any(id => !provided.Contains(NormalizeId(id))))
            return Reject("Detail packet is missing currently required mod details.");
        var hashes = summary.Mods.ToDictionary(mod => NormalizeId(mod.PackageId), mod => mod.Hash);
        if (details.Mods.Any(mod => !string.Equals(hashes[NormalizeId(mod.PackageId)],
            CompatibilityManifestSummaryBuilder.ComputeModHash(mod), StringComparison.OrdinalIgnoreCase)))
            return Reject("Mod details do not match their advertised summary.");
        var compared = CompatibilityManifestComparer.CompareSelected(server, details, options);
        return new(advertised.Issues.Concat(compared.Issues).ToArray());
    }

    private static CompatibilityComparisonResult Reject(string message)
        => new(new[] { Error(CompatibilityIssueCode.ModListMismatch, message, "mods") });
}
