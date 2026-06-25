namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record CompatibilityComparisonResult(IReadOnlyList<CompatibilityIssue> Issues)
{
    public bool Accepted => Issues.All(issue => issue.Severity != CompatibilityIssueSeverity.Error);
}
