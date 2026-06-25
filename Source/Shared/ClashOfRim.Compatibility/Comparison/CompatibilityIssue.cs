namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record CompatibilityIssue(
    CompatibilityIssueSeverity Severity,
    CompatibilityIssueCode Code,
    string Message,
    string? Subject = null);
