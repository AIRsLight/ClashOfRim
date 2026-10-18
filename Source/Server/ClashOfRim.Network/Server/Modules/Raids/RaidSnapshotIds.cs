using System.Security.Cryptography;
using System.Text;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal static class RaidSnapshotIds
{
    internal static string Settlement(string colonyId, string raidEventId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raidEventId));
        return SanitizeSnapshotIdPart(colonyId)
            + "-raid-settled-"
            + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string SanitizeSnapshotIdPart(string value)
    {
        char[] chars = (value ?? string.Empty).Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                chars[i] = '-';
            }
        }

        string sanitized = new(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "snapshot" : sanitized;
    }

    internal static string Cleanup(string colonyId, string raidEventId, DateTimeOffset nowUtc)
    {
        string safeColony = SanitizeSnapshotIdPart(colonyId);
        string safeRaid = SanitizeSnapshotIdPart(raidEventId);
        if (safeRaid.Length > 24)
        {
            safeRaid = safeRaid[..24];
        }

        return $"{safeColony}-raid-cleanup-{nowUtc:yyyyMMddHHmmss}-{safeRaid}";
    }

}
