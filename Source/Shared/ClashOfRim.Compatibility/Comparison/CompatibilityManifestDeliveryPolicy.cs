namespace AIRsLight.ClashOfRim.Compatibility;

public static class CompatibilityManifestDeliveryPolicy
{
    public static bool ShouldIncludeFullManifest(IEnumerable<CompatibilityIssue>? issues)
    {
        return issues?.Any(issue => issue.Severity != CompatibilityIssueSeverity.Info) == true;
    }
}
