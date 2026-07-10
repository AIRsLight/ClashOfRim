namespace AIRsLight.ClashOfRim.Compatibility;

// Versioning policy:
// - ProductVersion is the human-facing base build/release version. Release automation
//   uses this exact value for main releases, and appends branch plus commit metadata
//   for development prereleases. Do not include branch suffixes here.
//   Bump it for bug fixes, UI/text/icon changes, and feature releases even when
//   the network protocol is unchanged.
// - ProtocolVersion is the hard client/server compatibility gate. Bump it whenever an
//   older peer could corrupt authoritative state, misread ledgers, or apply snapshots
//   incorrectly if allowed to keep connecting.
// - ProtocolMajor changes for breaking protocol semantics or payload shapes.
// - ProtocolMinor changes for compatible additions such as optional fields, optional
//   endpoints, or ignorable capabilities. MinimumSupportedProtocolMinor defines the
//   oldest minor version this build intentionally accepts.
// - CompatibilityApiVersion is for compatibility-plugin APIs and hooks. Bump the minor
//   version for additive hooks; bump the major version when hook signatures, timing, or
//   required behavior changes.
// - Persistent data formats should have their own schema/package versions instead of
//   being inferred from ProductVersion. Examples include save snapshot packages, pawn
//   packages, server database schema, world baseline payloads, and plugin baseline data.
public static class ClashOfRimVersion
{
    public const string ProductName = "ClashOfRim";
    public const string ProductVersion = "0.1.3";
    public const string ProtocolVersion = "2026-07-10";
    public const int ProtocolMajor = 1;
    public const int ProtocolMinor = 2;
    public const int MinimumSupportedProtocolMajor = 1;
    public const int MinimumSupportedProtocolMinor = 2;
    public const string CompatibilityApiVersion = "1.0";

    public static string ProtocolDisplayVersion => $"{ProtocolMajor}.{ProtocolMinor}";
    public static string MinimumSupportedProtocolDisplayVersion =>
        $"{MinimumSupportedProtocolMajor}.{MinimumSupportedProtocolMinor}";

    public static bool IsProtocolCompatible(int major, int minor)
    {
        if (major != ProtocolMajor)
        {
            return false;
        }

        return minor >= MinimumSupportedProtocolMinor
            && minor <= ProtocolMinor;
    }
}
