using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AIRsLight.ClashOfRim.Compatibility;

public static class CompatibilityManifestSummaryBuilder
{
    public static CompatibilityManifestSummary Build(CompatibilityManifest manifest) => new()
    {
        SchemaVersion = manifest.SchemaVersion, ManifestId = manifest.ManifestId, ProtocolVersion = manifest.ProtocolVersion,
        RimWorldVersion = manifest.RimWorldVersion, GameLanguage = manifest.GameLanguage, DlcIds = manifest.DlcIds,
        ConfigSha256 = manifest.ConfigSha256, DefSummaries = manifest.DefSummaries, SupportsSelectedDetails = true,
        Mods = manifest.Mods.Select(mod => new ModManifestSummaryEntry
        {
            LoadOrder = mod.LoadOrder, PackageId = mod.PackageId, Name = mod.Name, Role = mod.Role, Hash = ComputeModHash(mod)
        }).ToArray()
    };

    // Keep the existing client's hash layout, including whether a config file exists.
    // Numeric formatting must not depend on the player's or server's current culture.
    public static string ComputeModHash(ModManifestEntry mod)
    {
        string body = string.Join("\n", new[] { mod.LoadOrder.ToString(CultureInfo.InvariantCulture), mod.PackageId,
                mod.Name, mod.Source, mod.WorkshopId, mod.Role.ToString() })
            + "\n" + string.Join("\n", mod.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.RelativePath + "|" + file.Size.ToString(CultureInfo.InvariantCulture) + "|" + file.Sha256 + "|" + file.Kind))
            + "\n" + string.Join("\n", mod.Configs.OrderBy(config => config.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(config => config.FileName + "|" + config.HasSavedFile + "|" + config.Sha256));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).Replace("-", "").ToLowerInvariant();
    }
}
