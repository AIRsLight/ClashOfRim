using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal static class ModWorldTileGeometryBinaryCodec
{
    public const string EncodingName = "WorldTileGeometryBinaryV1";

    private const uint Magic = 0x524F4354; // TCOR
    private const int Version = 1;

    public static byte[] Encode(ModWorldTileGeometryDto geometry)
    {
        if (geometry is null)
        {
            throw new ArgumentNullException(nameof(geometry));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(geometry.Layers.Count);

        foreach (ModWorldTileLayerGeometryDto layer in geometry.Layers.OrderBy(layer => layer.LayerId))
        {
            byte[] layerName = string.IsNullOrWhiteSpace(layer.LayerDefName)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(layer.LayerDefName!);
            List<ModWorldTileCenterDto> tileCenters = layer.TileCenters
                .OrderBy(center => center.Tile)
                .ToList();

            writer.Write(layer.LayerId);
            writer.Write(layer.AverageTileSize);
            writer.Write(tileCenters.Count);
            writer.Write(layerName.Length);
            writer.Write(layerName);

            foreach (ModWorldTileCenterDto center in tileCenters)
            {
                writer.Write(center.Tile);
                writer.Write(center.X);
                writer.Write(center.Y);
                writer.Write(center.Z);
            }
        }

        return stream.ToArray();
    }
}
