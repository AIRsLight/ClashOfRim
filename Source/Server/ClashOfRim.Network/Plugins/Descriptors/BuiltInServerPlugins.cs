namespace AIRsLight.ClashOfRim.Network.Plugins;

public static class BuiltInServerPlugins
{
    public static IReadOnlyList<ClashOfRimServerPluginDescriptor> Descriptors { get; } =
        BuiltInCoreServerPlugins.Descriptors
        .Concat(BuiltInDlcServerPlugins.Descriptors)
        .ToList();
}
