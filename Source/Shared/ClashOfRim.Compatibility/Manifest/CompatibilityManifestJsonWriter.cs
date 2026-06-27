using System.Text;

namespace AIRsLight.ClashOfRim.Compatibility;

public static class CompatibilityManifestJsonWriter
{
    public static string Write(CompatibilityManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var json = new StringBuilder();
        json.Append('{');
        WriteProperty(json, "schemaVersion", manifest.SchemaVersion);
        WriteProperty(json, "manifestId", manifest.ManifestId);
        WriteProperty(json, "protocolVersion", manifest.ProtocolVersion);
        WriteProperty(json, "rimWorldVersion", manifest.RimWorldVersion);
        WriteProperty(json, "gameLanguage", manifest.GameLanguage);
        WriteStringArrayProperty(json, "dlcIds", manifest.DlcIds);
        WriteProperty(json, "configVersion", manifest.ConfigVersion);
        WriteProperty(json, "configSha256", manifest.ConfigSha256);
        WriteMods(json, manifest.Mods);
        WriteDefs(json, manifest.DefSummaries);
        TrimTrailingComma(json);
        json.Append('}');
        return json.ToString();
    }

    public static string WriteSummary(CompatibilityManifestSummary summary)
    {
        if (summary is null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        var json = new StringBuilder();
        json.Append('{');
        WriteProperty(json, "schemaVersion", summary.SchemaVersion);
        WriteProperty(json, "manifestId", summary.ManifestId);
        WriteProperty(json, "protocolVersion", summary.ProtocolVersion);
        WriteProperty(json, "rimWorldVersion", summary.RimWorldVersion);
        WriteProperty(json, "gameLanguage", summary.GameLanguage);
        WriteStringArrayProperty(json, "dlcIds", summary.DlcIds);
        WriteProperty(json, "configSha256", summary.ConfigSha256);
        WriteSummaryMods(json, summary.Mods);
        WriteDefs(json, summary.DefSummaries);
        TrimTrailingComma(json);
        json.Append('}');
        return json.ToString();
    }

    private static void WriteMods(StringBuilder json, IReadOnlyList<ModManifestEntry> mods)
    {
        WriteName(json, "mods");
        json.Append('[');
        foreach (ModManifestEntry mod in mods)
        {
            json.Append('{');
            WriteProperty(json, "loadOrder", mod.LoadOrder);
            WriteProperty(json, "packageId", mod.PackageId);
            WriteProperty(json, "name", mod.Name);
            WriteProperty(json, "source", mod.Source);
            WriteProperty(json, "workshopId", mod.WorkshopId);
            WriteProperty(json, "workshopItemState", mod.WorkshopItemState);
            WriteProperty(json, "workshopLocalInstalledAtUnix", mod.WorkshopLocalInstalledAtUnix);
            WriteProperty(json, "role", mod.Role.ToString());
            WriteFiles(json, mod.Files);
            WriteConfigs(json, mod.Configs);
            TrimTrailingComma(json);
            json.Append("},");
        }

        TrimTrailingComma(json);
        json.Append("],");
    }

    private static void WriteSummaryMods(StringBuilder json, IReadOnlyList<ModManifestSummaryEntry> mods)
    {
        WriteName(json, "mods");
        json.Append('[');
        foreach (ModManifestSummaryEntry mod in mods)
        {
            json.Append('{');
            WriteProperty(json, "loadOrder", mod.LoadOrder);
            WriteProperty(json, "packageId", mod.PackageId);
            WriteProperty(json, "name", mod.Name);
            WriteProperty(json, "role", mod.Role.ToString());
            WriteProperty(json, "hash", mod.Hash);
            TrimTrailingComma(json);
            json.Append("},");
        }

        TrimTrailingComma(json);
        json.Append("],");
    }

    private static void WriteFiles(StringBuilder json, IReadOnlyList<ControlledFileEntry> files)
    {
        WriteName(json, "files");
        json.Append('[');
        foreach (ControlledFileEntry file in files)
        {
            json.Append('{');
            WriteProperty(json, "relativePath", file.RelativePath);
            WriteProperty(json, "size", file.Size);
            WriteProperty(json, "sha256", file.Sha256);
            WriteProperty(json, "lastWriteUtcUnix", file.LastWriteUtcUnix);
            WriteProperty(json, "kind", file.Kind.ToString());
            TrimTrailingComma(json);
            json.Append("},");
        }

        TrimTrailingComma(json);
        json.Append("],");
    }

    private static void WriteConfigs(StringBuilder json, IReadOnlyList<ModConfigDigest> configs)
    {
        WriteName(json, "configs");
        json.Append('[');
        foreach (ModConfigDigest config in configs)
        {
            json.Append('{');
            WriteProperty(json, "fileName", config.FileName);
            WriteProperty(json, "sha256", config.Sha256);
            WriteProperty(json, "canonicalXml", config.CanonicalXml);
            TrimTrailingComma(json);
            json.Append("},");
        }

        TrimTrailingComma(json);
        json.Append("],");
    }

    private static void WriteDefs(StringBuilder json, IReadOnlyList<DefSummary> defs)
    {
        WriteName(json, "defSummaries");
        json.Append('[');
        foreach (DefSummary def in defs)
        {
            json.Append('{');
            WriteProperty(json, "name", def.Name);
            WriteProperty(json, "count", def.Count);
            WriteProperty(json, "hash", def.Hash);
            TrimTrailingComma(json);
            json.Append("},");
        }

        TrimTrailingComma(json);
        json.Append("],");
    }

    private static void WriteStringArrayProperty(StringBuilder json, string name, IReadOnlyList<string> values)
    {
        WriteName(json, name);
        json.Append('[');
        foreach (string value in values)
        {
            WriteString(json, value);
            json.Append(',');
        }

        TrimTrailingComma(json);
        json.Append("],");
    }

    private static void WriteProperty(StringBuilder json, string name, string? value)
    {
        WriteName(json, name);
        WriteString(json, value ?? string.Empty);
        json.Append(',');
    }

    private static void WriteProperty(StringBuilder json, string name, int value)
    {
        WriteName(json, name);
        json.Append(value);
        json.Append(',');
    }

    private static void WriteProperty(StringBuilder json, string name, long value)
    {
        WriteName(json, name);
        json.Append(value);
        json.Append(',');
    }

    private static void WriteName(StringBuilder json, string name)
    {
        WriteString(json, name);
        json.Append(':');
    }

    private static void WriteString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char ch in value ?? string.Empty)
        {
            switch (ch)
            {
                case '\\':
                    json.Append("\\\\");
                    break;
                case '"':
                    json.Append("\\\"");
                    break;
                case '\n':
                    json.Append("\\n");
                    break;
                case '\r':
                    json.Append("\\r");
                    break;
                case '\t':
                    json.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        json.Append("\\u");
                        json.Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        json.Append(ch);
                    }
                    break;
            }
        }

        json.Append('"');
    }

    private static void TrimTrailingComma(StringBuilder json)
    {
        if (json.Length > 0 && json[json.Length - 1] == ',')
        {
            json.Length -= 1;
        }
    }
}
