using AIRsLight.ClashOfRim.Modules.Compatibility;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal sealed class ClientCompatibilityRepairRuntime : IClientCompatibilityRepairRuntime
{
    public string Apply(ClientCompatibilityRepair repair, string manifestJson)
    {
        string message;
        switch (repair)
        {
            case ClientCompatibilityRepair.Manifest: CompatibilityBaselineApplicator.TryApplyServerManifest(manifestJson, out message); break;
            case ClientCompatibilityRepair.ConfigOverlay: CompatibilityBaselineApplicator.TryApplyServerConfigOverlay(manifestJson, out message); break;
            default: CompatibilityBaselineApplicator.TryOverwriteLocalConfigs(manifestJson, out message); break;
        }
        return message;
    }
}
