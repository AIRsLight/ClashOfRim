using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;

internal static class CoreTradeMetadataServerCompatibility
{
    public static ClashOfRimServerPluginDescriptor Descriptor { get; } =
        new(
            Id: "builtin.core.trade-metadata",
            Name: "Built-in core trade metadata",
            Version: "1.0.0",
            AssemblyName: "AIRsLight.ClashOfRim.Network",
            FileName: string.Empty,
            Capabilities: new[] { "core-trade-metadata" },
            TradeThingMetadataMatchers: new[] { new CoreTradeThingMetadataMatcher() });
}
