using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Transport;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

// Compatibility bridge for the existing public provider fields. It reads hooks only;
// login, retry and session ownership are implemented by the modules.
internal sealed class ClientAuthenticationEnvironment : IClientCompatibilityManifestSource, IClientAuthenticationDiagnostics
{
    public string? Id => ClashOfRimModNetworkClient.CompatibilityManifestIdProvider?.Invoke();
    public string? SummaryJson => ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider?.Invoke();
    public string NotConfiguredMessage => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured");
    public string ManifestBuildFailedMessage => ClashOfRimText.Key("ClashOfRim.Compatibility.ManifestBuildFailed");

    public string? BuildJson(IReadOnlyCollection<string>? packageIds)
    {
        string? requested = ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider?.Invoke(packageIds);
        string? json = string.IsNullOrWhiteSpace(requested) ? ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider?.Invoke() : requested;
        if (string.IsNullOrWhiteSpace(json))
            Log.Warning("[ClashOfRim][Compatibility] Full manifest retry was requested, but the local manifest provider returned no payload.");
        return json;
    }

    public void CleanupFailed(string? errorCode, string? message) =>
        Log.Warning("[ClashOfRim] Temporary setup session cleanup failed: " + errorCode + ": " + message);
}
