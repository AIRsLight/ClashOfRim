using AIRsLight.ClashOfRim.Compatibility;
using System;
using System.Linq;
using System.Text;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal static class ClientCompatibilityManifestDiagnostics
{
    public static string FormatForServerEntry(CompatibilityManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var text = new StringBuilder();
        text.Append("[ClashOfRim][Compatibility] Server entry mod list: mods=")
            .Append(manifest.Mods.Count)
            .Append("; manifest=").Append(Sanitize(manifest.ManifestId))
            .Append("; protocol=").Append(Sanitize(manifest.ProtocolVersion))
            .Append("; RimWorld=").Append(Sanitize(manifest.RimWorldVersion))
            .Append("; language=").Append(Sanitize(manifest.GameLanguage));

        foreach (ModManifestEntry mod in manifest.Mods.OrderBy(mod => mod.LoadOrder))
        {
            text.AppendLine()
                .Append("[ClashOfRim][Compatibility] [").Append(mod.LoadOrder).Append("] ")
                .Append(Sanitize(mod.PackageId))
                .Append(" | name=").Append(Sanitize(mod.Name))
                .Append(" | source=").Append(Sanitize(mod.Source))
                .Append(" | workshop=").Append(Sanitize(mod.WorkshopId))
                .Append(" | role=").Append(mod.Role);
        }

        return text.ToString();
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value!.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
    }
}
