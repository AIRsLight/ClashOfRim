namespace AIRsLight.ClashOfRim.Compatibility;

public sealed record CompatibilityComparisonOptions
{
    public static CompatibilityComparisonOptions Default { get; } = new();

    public bool AllowExtraPureTranslationMods { get; init; } = true;

    public IReadOnlyList<ModConfigComparisonRule> ModConfigRules { get; init; } = [];

    public ModConfigComparisonMode ResolveConfigMode(string packageId, string? fileName = null)
    {
        ModConfigComparisonRule? best = null;
        foreach (ModConfigComparisonRule rule in ModConfigRules)
        {
            if (!string.Equals(NormalizeId(rule.PackageId), NormalizeId(packageId), StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.FileName)
                && !string.Equals(rule.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null
                || (!string.IsNullOrWhiteSpace(rule.FileName) && string.IsNullOrWhiteSpace(best.FileName)))
            {
                best = rule;
            }
        }

        return best?.Mode ?? ModConfigComparisonMode.Enforce;
    }

    private static string NormalizeId(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
