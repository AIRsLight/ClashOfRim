using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal static class CompatibilityLanguageMismatchPolicy
{
    public static bool IsLanguageMismatch(string? code)
    {
        return string.Equals(
            code,
            nameof(CompatibilityIssueCode.GameLanguageMismatch),
            StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveServerLanguage(string? authoritativeWorldLanguage, string? manifestLanguage)
    {
        return !string.IsNullOrWhiteSpace(authoritativeWorldLanguage)
            ? authoritativeWorldLanguage!.Trim()
            : (manifestLanguage ?? string.Empty).Trim();
    }

    public static bool CanContinue(ModLoginResponseDto? response)
    {
        if (response?.Result?.Accepted != true
            || response.CompatibilityIssues is not { Count: > 0 } issues)
        {
            return false;
        }

        bool foundLanguageMismatch = false;
        foreach (ModCompatibilityIssueDto issue in issues)
        {
            if (IsLanguageMismatch(issue.Code))
            {
                foundLanguageMismatch = true;
                if (string.Equals(issue.Severity, nameof(CompatibilityIssueSeverity.Error), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(
                    issue.Code,
                    nameof(CompatibilityIssueCode.AllowedPureTranslationMod),
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    issue.Severity,
                    nameof(CompatibilityIssueSeverity.Info),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return foundLanguageMismatch;
    }
}
