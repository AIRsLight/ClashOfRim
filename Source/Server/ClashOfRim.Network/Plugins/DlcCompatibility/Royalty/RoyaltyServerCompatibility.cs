using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal static class RoyaltyServerCompatibility
{
    public static ClashOfRimServerPluginDescriptor Descriptor { get; } =
        new(
            Id: "builtin.dlc." + RoyaltyCompatibilityKeys.PackageId,
            Name: "Built-in DLC compatibility: Royalty",
            Version: "1.0.0",
            AssemblyName: "AIRsLight.ClashOfRim.Network",
            FileName: string.Empty,
            Capabilities: new[]
            {
                RoyaltyCompatibilityKeys.RemoteStateGuards,
                RoyaltyCompatibilityKeys.TradeMetadata,
                RoyaltyCompatibilityKeys.WeaponTraitMarketValueBaseline
            },
            AdminBaselineRequirementProviders: new IAdminBaselineRequirementProvider[]
            {
                new RoyaltyAdminBaselineRequirementProvider()
            },
            RequiredPackageIds: new[] { RoyaltyCompatibilityKeys.PackageId },
            Order: 100);

    private sealed class RoyaltyAdminBaselineRequirementProvider : IAdminBaselineRequirementProvider
    {
        public IEnumerable<AdminBaselineExtensionRequirement> GetRequirements(AdminBaselineRequirementContext context)
        {
            if (context.ServerPackageIds.Contains(RoyaltyCompatibilityKeys.PackageId))
            {
                yield return new AdminBaselineExtensionRequirement(
                    RoyaltyCompatibilityKeys.PackageId,
                    RoyaltyCompatibilityKeys.WeaponTraitMarketValueBaseline,
                    RoyaltyCompatibilityKeys.PackageId,
                    "Royalty persona weapon trait market values");
            }
        }
    }
}
