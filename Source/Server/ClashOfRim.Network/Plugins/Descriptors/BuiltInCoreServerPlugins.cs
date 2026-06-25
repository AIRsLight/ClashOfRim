namespace AIRsLight.ClashOfRim.Network.Plugins;

public static class BuiltInCoreServerPlugins
{
    public static IReadOnlyList<ClashOfRimServerPluginDescriptor> Descriptors { get; } = DiscoverDescriptors();

    private static IReadOnlyList<ClashOfRimServerPluginDescriptor> DiscoverDescriptors()
    {
        const string builtInCoreNamespace = "AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility";
        return BuiltInServerPluginDescriptorDiscovery.Discover(typeof(BuiltInCoreServerPlugins), builtInCoreNamespace);
    }
}
