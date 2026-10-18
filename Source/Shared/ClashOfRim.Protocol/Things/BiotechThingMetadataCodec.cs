using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIRsLight.ClashOfRim.Protocol;

public static class BiotechThingMetadataCodec
{
    public static IReadOnlyList<string> ReadList(IReadOnlyDictionary<string, string?>? metadata, string key)
    {
        string? value = ReadText(metadata, key);
        return NormalizeList(value?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
    }

    public static void WriteList(IDictionary<string, string?> metadata, string key, IEnumerable<string>? values)
    {
        string[] normalized = NormalizeList(values);
        WriteText(metadata, key, normalized.Length == 0 ? null : string.Join(",", normalized));
    }

    public static string? ReadText(IReadOnlyDictionary<string, string?>? metadata, string key)
    {
        return metadata is not null && metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    public static void WriteText(IDictionary<string, string?> metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            metadata.Remove(key);
        }
        else
        {
            metadata[key] = value;
        }
    }

    public static int SourceCount(IReadOnlyDictionary<string, string?>? metadata)
    {
        return int.TryParse(ReadText(metadata, BiotechThingMetadataKeys.ReproductiveSourceCount),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0
                ? Math.Min(count, BiotechThingMetadataKeys.MaxReproductiveSources)
                : 0;
    }

    public static string SourceKey(int index, string field)
    {
        return BiotechThingMetadataKeys.ReproductiveSourcePrefix + index.ToString(CultureInfo.InvariantCulture) + "." + field;
    }

    private static string[] NormalizeList(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
