using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class ServerHelloResponse
{
    public ServerHelloResponse(
        ProtocolResponse result,
        string productName,
        string productVersion,
        string protocolVersion,
        int protocolMajor,
        int protocolMinor,
        int minimumSupportedProtocolMajor,
        int minimumSupportedProtocolMinor,
        string compatibilityApiVersion,
        string serverTimeUtc,
        IReadOnlyList<ServerPluginVersionDto>? plugins = null)
    {
        Result = result;
        ProductName = productName;
        ProductVersion = productVersion;
        ProtocolVersion = protocolVersion;
        ProtocolMajor = protocolMajor;
        ProtocolMinor = protocolMinor;
        MinimumSupportedProtocolMajor = minimumSupportedProtocolMajor;
        MinimumSupportedProtocolMinor = minimumSupportedProtocolMinor;
        CompatibilityApiVersion = compatibilityApiVersion;
        ServerTimeUtc = serverTimeUtc;
        Plugins = plugins ?? Array.Empty<ServerPluginVersionDto>();
    }

    public ProtocolResponse Result { get; }

    public string ProductName { get; }

    public string ProductVersion { get; }

    public string ProtocolVersion { get; }

    public int ProtocolMajor { get; }

    public int ProtocolMinor { get; }

    public int MinimumSupportedProtocolMajor { get; }

    public int MinimumSupportedProtocolMinor { get; }

    public string CompatibilityApiVersion { get; }

    public string ServerTimeUtc { get; }

    public IReadOnlyList<ServerPluginVersionDto> Plugins { get; }
}

public sealed class ServerPluginVersionDto
{
    public ServerPluginVersionDto(
        string id,
        string name,
        string version,
        IReadOnlyList<string>? capabilities = null)
    {
        Id = id;
        Name = name;
        Version = version;
        Capabilities = capabilities ?? Array.Empty<string>();
    }

    public string Id { get; }

    public string Name { get; }

    public string Version { get; }

    public IReadOnlyList<string> Capabilities { get; }
}
