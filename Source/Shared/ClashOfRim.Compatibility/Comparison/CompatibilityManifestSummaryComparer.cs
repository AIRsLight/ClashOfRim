using static AIRsLight.ClashOfRim.Compatibility.CompatibilityManifestRules;

namespace AIRsLight.ClashOfRim.Compatibility;

public static class CompatibilityManifestSummaryComparer
{
    public static CompatibilitySummaryComparisonResult Compare(CompatibilityManifest server, CompatibilityManifestSummary client,
        CompatibilityComparisonOptions options)
    {
        var issues = new List<CompatibilityIssue>();
        var requested = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool verifyExtras = false;
        CompareHeader(issues, server, client);
        CompatibilityModSetComparison.Compare(issues, server.Mods, client.Mods,
            _ => options.AllowExtraPureTranslationMods,
            (baseline, candidate) =>
            {
                if (!string.Equals(CompatibilityManifestSummaryBuilder.ComputeModHash(baseline), candidate.Hash, StringComparison.OrdinalIgnoreCase))
                    requested.Add(baseline.PackageId);
            },
            // A declared role cannot prove file contents. Request the full manifest so
            // the normal classifier validates the extra mod before accepting it.
            _ => verifyExtras = true);
        CompareDefSummaries(issues, server.DefSummaries, client.DefSummaries);
        if (issues.Any(issue => issue.Severity == CompatibilityIssueSeverity.Error)) return new(false, false, Array.Empty<string>(), issues);
        if (verifyExtras) return new(false, true, new[] { "*" }, issues);
        if (requested.Count > 0) return new(false, true, client.SupportsSelectedDetails ? requested.ToArray() : new[] { "*" }, issues);
        return new(true, false, Array.Empty<string>(), issues);
    }
}

public sealed record CompatibilitySummaryComparisonResult(bool Accepted, bool RequiresFullManifest,
    IReadOnlyList<string> RequestedPackageIds, IReadOnlyList<CompatibilityIssue> Issues);
