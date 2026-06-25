using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

internal sealed class BiotechWorldPollutionExtensionProvider : IWorldTileFloatLayerExtensionProvider
{
    private static readonly IReadOnlyList<WorldConfigurationExtensionKey> Keys = new[]
    {
        new WorldConfigurationExtensionKey(
            BiotechCompatibilityKeys.PackageId,
            BiotechCompatibilityKeys.WorldGeneration),
        new WorldConfigurationExtensionKey(
            BiotechCompatibilityKeys.PackageId,
            BiotechCompatibilityKeys.WorldPollution)
    };

    public IReadOnlyList<WorldConfigurationExtensionKey> HandledKeys => Keys;

    public IReadOnlyList<string> HandledTileFloatLayers { get; } = new[]
    {
        BiotechCompatibilityKeys.WorldPollution
    };

    public IReadOnlyList<string> ConfirmOnAcceptedSnapshotLayers { get; } = new[]
    {
        BiotechCompatibilityKeys.WorldPollution
    };

    public IReadOnlyList<WorldConfigurationExtensionDto> NormalizeSubmittedExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming)
    {
        return BiotechServerCompatibility.BuildExtensions(
            BiotechServerCompatibility.ReadWorldGenerationSettings(incoming),
            BiotechServerCompatibility.ReadWorldPollution(incoming));
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> MergeExtensions(
        WorldConfigurationExtensionContext context,
        IReadOnlyList<WorldConfigurationExtensionDto> current,
        IReadOnlyList<WorldConfigurationExtensionDto> incoming)
    {
        BiotechWorldGenerationSettingsDto? incomingSettings = BiotechServerCompatibility.ReadWorldGenerationSettings(incoming);
        IReadOnlyList<WorldPollutedTileDto> incomingPollution = BiotechServerCompatibility.ReadWorldPollution(incoming);
        return BiotechServerCompatibility.BuildExtensions(
            incomingSettings ?? BiotechServerCompatibility.ReadWorldGenerationSettings(current),
            incomingPollution.Count > 0 ? incomingPollution : BiotechServerCompatibility.ReadWorldPollution(current));
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> RemoveColonyExtensions(
        string userId,
        string colonyId,
        IReadOnlyList<WorldConfigurationExtensionDto> current)
    {
        return BuildDeliveryExtensions(current);
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildDeliveryExtensions(
        IReadOnlyList<WorldConfigurationExtensionDto> current)
    {
        return BiotechServerCompatibility.BuildExtensions(
            BiotechServerCompatibility.ReadWorldGenerationSettings(current),
            BiotechServerCompatibility.ReadWorldPollution(current));
    }

    public IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensionsFromAcceptedSnapshot(
        WorldConfigurationExtensionSnapshotContext context)
    {
        return Array.Empty<WorldConfigurationExtensionDto>();
    }

    public IReadOnlyList<WorldTileFloatLayerValue> ReadTileFloatLayer(
        string layerId,
        IReadOnlyList<WorldConfigurationExtensionDto> extensions)
    {
        return string.Equals(layerId, BiotechCompatibilityKeys.WorldPollution, StringComparison.Ordinal)
            ? BiotechServerCompatibility.ReadWorldPollution(extensions)
                .Select(tile => new WorldTileFloatLayerValue(tile.Tile, tile.Pollution))
                .ToList()
            : Array.Empty<WorldTileFloatLayerValue>();
    }

    public WorldConfigurationExtensionDto? BuildTileFloatLayerExtension(
        string layerId,
        IReadOnlyList<WorldTileFloatLayerValue> values)
    {
        return string.Equals(layerId, BiotechCompatibilityKeys.WorldPollution, StringComparison.Ordinal)
            ? BiotechServerCompatibility.BuildWorldPollutionExtension(values
                .Where(value => value.Tile >= 0 && value.Value > 0f)
                .Select(value => new WorldPollutedTileDto(value.Tile, Math.Clamp(value.Value, 0f, 1f)))
                .ToList())
            : null;
    }

    public IReadOnlyList<WorldTileFloatLayerValue> ProjectTileFloatLayerFromSave(string layerId, byte[] saveBytes)
    {
        if (!string.Equals(layerId, BiotechCompatibilityKeys.WorldPollution, StringComparison.Ordinal))
        {
            return Array.Empty<WorldTileFloatLayerValue>();
        }

        using var stream = new MemoryStream(saveBytes);
        XDocument document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XElement? layer = document.Root?
            .Element("game")?
            .Element("world")?
            .Element("grid")?
            .Element("layers")?
            .Element("values")?
            .Elements("li")
            .FirstOrDefault(item => string.Equals(item.Element("def")?.Value.Trim(), "Surface", StringComparison.OrdinalIgnoreCase))
            ?? document.Root?
                .Element("game")?
                .Element("world")?
                .Element("grid")?
                .Element("layers")?
                .Element("values")?
                .Elements("li")
                .FirstOrDefault(item => item.Element("tilePollutionDeflate") is not null || item.Element("tilePollution") is not null);

        if (layer is null)
        {
            return Array.Empty<WorldTileFloatLayerValue>();
        }

        byte[] bytes = ReadCompressedByteArray(layer, "tilePollution");
        List<WorldTileFloatLayerValue> pollutedTiles = new();
        for (int tile = 0; tile + tile + 1 < bytes.Length; tile++)
        {
            int offset = tile * 2;
            ushort raw = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            if (raw == 0)
            {
                continue;
            }

            pollutedTiles.Add(new WorldTileFloatLayerValue(tile, raw / 65535f));
        }

        return pollutedTiles;
    }

    public WorldTileFloatLayerIncreaseNotification? BuildIncreaseNotification(
        WorldTileFloatLayerNotificationContext context,
        WorldTileFloatLayerIncrease increase)
    {
        if (!string.Equals(increase.LayerId, BiotechCompatibilityKeys.WorldPollution, StringComparison.Ordinal))
        {
            return null;
        }

        return new WorldTileFloatLayerIncreaseNotification(
            "world-pollution",
            ServerLocalization.Text("WorldPollution.NotificationTitle"),
            ServerLocalization.Text(
                "WorldPollution.NotificationMessage",
                new Dictionary<string, string?>
                {
                    ["ACTOR"] = context.ActorLabel,
                    ["TILE"] = increase.Tile.ToString(CultureInfo.InvariantCulture),
                    ["DELTA"] = FormatPollutionPercent(increase.Delta)
                }),
            ServerNotificationSeverity.Warning,
            RadiusTiles: 4);
    }

    private static byte[] ReadCompressedByteArray(XElement parent, string label)
    {
        XElement? deflated = parent.Element(label + "Deflate");
        if (deflated is not null && !string.IsNullOrWhiteSpace(deflated.Value))
        {
            byte[] compressed = Convert.FromBase64String(RemoveWhitespace(deflated.Value));
            using var source = new MemoryStream(compressed);
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            deflate.CopyTo(target);
            return target.ToArray();
        }

        XElement? raw = parent.Element(label);
        if (raw is null || string.IsNullOrWhiteSpace(raw.Value))
        {
            return Array.Empty<byte>();
        }

        return Convert.FromBase64String(RemoveWhitespace(raw.Value));
    }

    private static string RemoveWhitespace(string value)
    {
        return string.Concat(value.Where(ch => !char.IsWhiteSpace(ch)));
    }

    private static string FormatPollutionPercent(float value)
    {
        float percent = Math.Max(0f, value) * 100f;
        return percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }
}
