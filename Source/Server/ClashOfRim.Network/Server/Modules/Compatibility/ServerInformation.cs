using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Compatibility;

internal sealed record ServerPluginDetails(string Id, string Name, string Version, string AssemblyName,
    string FileName, IReadOnlyList<string> Capabilities);
internal sealed record ServerPluginListing(IReadOnlyList<ServerPluginDetails> Plugins);

internal sealed class ServerInformation(ServerPluginRegistry plugins)
{
    internal ServerHelloResponse Hello() => new(
        ProtocolResponse.Ok(), ClashOfRimVersion.ProductName, ClashOfRimVersion.ProductVersion,
        ProtocolApiVersion.Current, ProtocolApiVersion.Major, ProtocolApiVersion.Minor,
        ProtocolApiVersion.MinimumSupportedMajor, ProtocolApiVersion.MinimumSupportedMinor,
        ClashOfRimVersion.CompatibilityApiVersion, DateTimeOffset.UtcNow.ToString("O"),
        plugins.Plugins.Select(plugin => new ServerPluginVersionDto(plugin.Id, plugin.Name, plugin.Version, plugin.Capabilities)).ToArray());

    internal ServerPluginListing ListPlugins() => new(plugins.Plugins.Select(plugin => new ServerPluginDetails(
        plugin.Id, plugin.Name, plugin.Version, plugin.AssemblyName, plugin.FileName, plugin.Capabilities)).ToArray());
}
