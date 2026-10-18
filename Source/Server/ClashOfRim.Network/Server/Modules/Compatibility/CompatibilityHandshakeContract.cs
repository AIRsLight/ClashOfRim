using System.Text.Json;
using System.Text.Json.Serialization;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Compatibility;

internal static class CompatibilityHandshakeContract
{
    internal static string InvalidManifest(string reason) => ServerLocalization.Text("Compatibility.InvalidManifestJson",
        new Dictionary<string, string?> { ["MESSAGE"] = reason });

    private static readonly JsonSerializerOptions CompatibilityJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    internal static bool TryReadCompatibilityManifest(
        string? manifestJson,
        out CompatibilityManifest? manifest,
        out string failure)
    {
        manifest = null;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return true;
        }

        try
        {
            manifest = JsonSerializer.Deserialize<CompatibilityManifest>(manifestJson, CompatibilityJsonOptions);
            if (manifest is null)
            {
                failure = ServerLocalization.Text("Compatibility.EmptyManifest");
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            failure = InvalidManifest(ex.Message);
            return false;
        }
    }

    internal static bool TryReadCompatibilityManifestSummary(
        string? summaryJson,
        out CompatibilityManifestSummary? summary,
        out string failure)
    {
        summary = null;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            failure = ServerLocalization.Text("Compatibility.ManifestMissing");
            return false;
        }

        try
        {
            summary = JsonSerializer.Deserialize<CompatibilityManifestSummary>(summaryJson, CompatibilityJsonOptions);
            if (summary is null)
            {
                failure = ServerLocalization.Text("Compatibility.EmptyManifest");
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            failure = InvalidManifest(ex.Message);
            return false;
        }
    }

    internal static string Serialize(CompatibilityManifest manifest, CompatibilityComparisonOptions options)
    {
        CompatibilityManifest enriched = manifest with
        {
            ModConfigRules = options.ModConfigRules
        };
        return JsonSerializer.Serialize(enriched, CompatibilityJsonOptions);
    }

    internal static IReadOnlyList<CompatibilityIssueDto> ToCompatibilityIssueDtos(
        IReadOnlyList<CompatibilityIssue> issues)
    {
        return issues
            .Select(issue => new CompatibilityIssueDto(
                issue.Severity.ToString(),
                issue.Code.ToString(),
                issue.Message,
                issue.Subject))
            .ToList();
    }
}
