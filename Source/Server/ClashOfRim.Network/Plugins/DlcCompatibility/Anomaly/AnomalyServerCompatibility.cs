namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal static class AnomalyServerCompatibility
{
    public static ClashOfRimServerPluginDescriptor Descriptor { get; } =
        new(
            Id: "builtin.dlc." + AnomalyCompatibilityKeys.PackageId,
            Name: "Built-in DLC compatibility: Anomaly",
            Version: "1.0.0",
            AssemblyName: "AIRsLight.ClashOfRim.Network",
            FileName: string.Empty,
            Capabilities: new[]
            {
                AnomalyCompatibilityKeys.RemoteStateGuards,
                AnomalyCompatibilityKeys.TradeMetadata
            },
            TradeThingMetadataMatchers: new AIRsLight.ClashOfRim.Protocol.ITradeThingMetadataMatcher[]
            {
                new AnomalyTradeThingMetadataMatcher()
            },
            RequiredPackageIds: new[] { AnomalyCompatibilityKeys.PackageId },
            Order: 400);
}
