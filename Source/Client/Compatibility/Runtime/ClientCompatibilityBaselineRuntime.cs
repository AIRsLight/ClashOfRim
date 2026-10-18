using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal sealed class ClientCompatibilityBaselineRuntime : IClientCompatibilityBaselineRuntime
{
    public void ApplyDlcIds(IReadOnlyList<string>? ids) => ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(ids);
    public void InvalidateManifestCache() => ModSettingsBaselinePolicy.InvalidateManifestCache();
    public void Diagnose(string message) => Log.Warning("[ClashOfRim][Compatibility] " + message);
}
