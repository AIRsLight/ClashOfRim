using AIRsLight.ClashOfRim.Compatibility;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public static class ProtocolApiVersion
{
    public const string Current = ClashOfRimVersion.ProtocolVersion;
    public const int Major = ClashOfRimVersion.ProtocolMajor;
    public const int Minor = ClashOfRimVersion.ProtocolMinor;
    public const int MinimumSupportedMajor = ClashOfRimVersion.MinimumSupportedProtocolMajor;
    public const int MinimumSupportedMinor = ClashOfRimVersion.MinimumSupportedProtocolMinor;

    public static bool IsCompatible(int major, int minor)
    {
        return ClashOfRimVersion.IsProtocolCompatible(major, minor);
    }

    public static ProtocolResponse? Validate(string? clientVersion) => clientVersion == Current ? null
        : ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion,
            ServerLocalization.Text("Protocol.IncompatibleVersionDetails", new Dictionary<string, string?>
                { ["CLIENT"] = clientVersion, ["SERVER"] = Current, ["PRODUCT"] = ClashOfRimVersion.ProductVersion }));
}
