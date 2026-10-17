using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal interface IClientCompatibilityManifestSource
{
    string? Id { get; }
    string? SummaryJson { get; }
    string? BuildJson(IReadOnlyCollection<string>? packageIds);
}

internal interface IClientAuthenticationDiagnostics
{
    string NotConfiguredMessage { get; }
    string ManifestBuildFailedMessage { get; }
    void CleanupFailed(string? errorCode, string? message);
}
