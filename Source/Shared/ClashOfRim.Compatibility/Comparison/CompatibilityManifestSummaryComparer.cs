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
        var matches = CompatibilityModSetComparison.Compare(issues, server.Mods, client.Mods,
            _ => options.AllowExtraPureTranslationMods,
            // A declared role cannot prove file contents. Request the full manifest so
            // the normal classifier validates the extra mod before accepting it.
            _ => verifyExtras = true);
        if (issues.Any(issue => issue.Severity == CompatibilityIssueSeverity.Error)) return new(false, false, Array.Empty<string>(), issues);
        if (verifyExtras) return new(false, true, new[] { "*" }, issues);
        foreach (var pair in matches)
            if (!string.Equals(CompatibilityManifestSummaryBuilder.ComputeModHash(pair.Server), pair.Client.Hash, StringComparison.OrdinalIgnoreCase))
                requested.Add(pair.Server.PackageId);
        // The existing digest combines files and config. Detailed comparison orders
        // those checks; do not let derived Def differences short-circuit that exchange.
        if (requested.Count > 0) return new(false, true, client.SupportsSelectedDetails ? requested.ToArray() : new[] { "*" }, issues);
        CompareDefSummaries(issues, server.DefSummaries, client.DefSummaries);
        if (issues.Any(issue => issue.Severity == CompatibilityIssueSeverity.Error)) return new(false, false, Array.Empty<string>(), issues);
        return new(true, false, Array.Empty<string>(), issues);
    }
}

public sealed record CompatibilitySummaryComparisonResult(bool Accepted, bool RequiresFullManifest,
    IReadOnlyList<string> RequestedPackageIds, IReadOnlyList<CompatibilityIssue> Issues);
