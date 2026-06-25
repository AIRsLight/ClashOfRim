using AIRsLight.ClashOfRim.Compatibility;

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
}
