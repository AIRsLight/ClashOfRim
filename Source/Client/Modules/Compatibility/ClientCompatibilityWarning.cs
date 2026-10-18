using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal static class ClientCompatibilityWarning
{
    internal static ModLoginResponseDto FromPreparation(ModPrepareWorldSessionResponseDto response) => new()
    {
        Result = response.Result, ServerCompatibilityManifestJson = response.ServerCompatibilityManifestJson,
        CompatibilityIssues = response.CompatibilityIssues ?? new(), CanOverrideCompatibilityBaseline = response.CanOverrideCompatibilityBaseline
    };
    internal static bool ShouldShow(ModLoginResponseDto? response, bool languageAccepted)
    {
        if (response is null || (languageAccepted && CompatibilityLanguageMismatchPolicy.CanContinue(response))) return false;
        if (response.CompatibilityIssues is { Count: > 0 })
            return response.Result?.Accepted != true || response.CompatibilityIssues.Any(issue => !string.Equals(issue.Severity, "Info", StringComparison.OrdinalIgnoreCase));
        return response.Result?.Accepted != true && response.Result?.ErrorCode == (int)ProtocolErrorCode.ValidationFailed
            && !string.IsNullOrWhiteSpace(response.ServerCompatibilityManifestJson);
    }
}
