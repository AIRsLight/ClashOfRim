using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal static class OdysseyServerCompatibility
{
    public static ClashOfRimServerPluginDescriptor Descriptor { get; } =
        new(
            Id: "builtin.dlc." + OdysseyCompatibilityKeys.PackageId,
            Name: "Built-in DLC compatibility: Odyssey",
            Version: "1.0.0",
            AssemblyName: "AIRsLight.ClashOfRim.Network",
            FileName: string.Empty,
            Capabilities: new[]
            {
                OdysseyCompatibilityKeys.RemoteStateGuards,
                OdysseyCompatibilityKeys.TradeMetadata,
                OdysseyCompatibilityKeys.WeaponTraitMarketValueBaseline
            },
            AdminBaselineRequirementProviders: new IAdminBaselineRequirementProvider[]
            {
                new OdysseyAdminBaselineRequirementProvider()
            },
            WorldObjectClassifiers: new IWorldObjectClassifier[] { new OdysseyWorldObjectClassifier() },
            RequiredPackageIds: new[] { OdysseyCompatibilityKeys.PackageId },
            Order: 500);

    private sealed class OdysseyAdminBaselineRequirementProvider : IAdminBaselineRequirementProvider
    {
        public IEnumerable<AdminBaselineExtensionRequirement> GetRequirements(AdminBaselineRequirementContext context)
        {
            if (context.ServerPackageIds.Contains(OdysseyCompatibilityKeys.PackageId))
            {
                yield return new AdminBaselineExtensionRequirement(
                    OdysseyCompatibilityKeys.PackageId,
                    OdysseyCompatibilityKeys.WeaponTraitMarketValueBaseline,
                    OdysseyCompatibilityKeys.PackageId,
                    "Odyssey specialized weapon trait market values");
            }
        }
    }

    private sealed class OdysseyWorldObjectClassifier : IWorldObjectClassifier
    {
        public bool IsOrbitalWorldObject(WorldObjectSummary worldObject)
        {
            return ContainsGravshipToken(worldObject.Class)
                || ContainsGravshipToken(worldObject.Def)
                || ContainsGravshipToken(worldObject.Name)
                || ContainsGravshipToken(worldObject.Tile);
        }

        public bool IsPlayerColonyAnchor(WorldObjectSummary worldObject)
        {
            // The caller only asks this classifier after the map has been
            // marked as spawned by a gravship landing. In that context the map
            // parent, including quest sites and NPC bases, is the new colony
            // anchor for relocation continuity.
            return true;
        }

        private static bool ContainsGravshipToken(string? value)
        {
            return ContainsToken(value, "Gravship");
        }

        private static bool ContainsToken(string? value, string token)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Contains(token, StringComparison.OrdinalIgnoreCase);
        }
    }
}
