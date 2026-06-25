using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModServerHelloResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "productName")]
    public string ProductName { get; set; } = string.Empty;

    [DataMember(Name = "productVersion")]
    public string ProductVersion { get; set; } = string.Empty;

    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = string.Empty;

    [DataMember(Name = "protocolMajor")]
    public int ProtocolMajor { get; set; }

    [DataMember(Name = "protocolMinor")]
    public int ProtocolMinor { get; set; }

    [DataMember(Name = "minimumSupportedProtocolMajor")]
    public int MinimumSupportedProtocolMajor { get; set; }

    [DataMember(Name = "minimumSupportedProtocolMinor")]
    public int MinimumSupportedProtocolMinor { get; set; }

    [DataMember(Name = "compatibilityApiVersion")]
    public string CompatibilityApiVersion { get; set; } = string.Empty;

    [DataMember(Name = "serverTimeUtc")]
    public string ServerTimeUtc { get; set; } = string.Empty;

    [DataMember(Name = "plugins")]
    public List<ModServerPluginVersionDto> Plugins { get; set; } = new();
}

[DataContract]
public sealed class ModServerPluginVersionDto
{
    [DataMember(Name = "id")]
    public string Id { get; set; } = string.Empty;

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "version")]
    public string Version { get; set; } = string.Empty;

    [DataMember(Name = "capabilities")]
    public List<string> Capabilities { get; set; } = new();
}
