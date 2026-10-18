using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Modules.Transport;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal sealed class ClientLocalCompatibilityManifest : IClientCompatibilityManifestSource
{
    public string? Id => Read(() => ClientCompatibilityManifestBuilder.Build().ManifestId, "id");
    public string? SummaryJson => Read(ClientCompatibilityManifestBuilder.BuildSummaryJson, "summary");
    public string? BuildJson(IReadOnlyCollection<string>? packageIds)
        => Read(() => packageIds is null ? ClientCompatibilityManifestBuilder.BuildJson() : ClientCompatibilityManifestBuilder.BuildJsonForPackages(packageIds), "payload");
    internal void LogEntry() => Log.Message(ClientCompatibilityManifestDiagnostics.FormatForServerEntry(ClientCompatibilityManifestBuilder.Build()));
    private static string? Read(Func<string?> read, string part)
    {
        try { return read(); }
        catch (Exception ex) { Log.Warning("[ClashOfRim][Compatibility] Failed to build client manifest " + part + ": " + ex); return null; }
    }
}
