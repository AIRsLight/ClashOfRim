namespace AIRsLight.ClashOfRim.Network.Plugins;

public static class BuiltInDlcServerPlugins
{
    public static IReadOnlyList<ClashOfRimServerPluginDescriptor> Descriptors { get; } = DiscoverDescriptors();

    private static IReadOnlyList<ClashOfRimServerPluginDescriptor> DiscoverDescriptors()
    {
        const string builtInDlcNamespace = "AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility";
        return BuiltInServerPluginDescriptorDiscovery.Discover(typeof(BuiltInDlcServerPlugins), builtInDlcNamespace);
    }
}
