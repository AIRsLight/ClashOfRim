using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIRsLight.ClashOfRim.Protocol;

public static class WorldTileGeometryBinaryCodec
{
    private const uint Magic = 0x524F4354; // TCOR
    private const int Version = 1;
    private const int HeaderSize = 12;
    private const int LayerHeaderSize = 16;
    private const int TileCenterSize = 16;

    public static byte[]? Encode(WorldTileGeometryDto? geometry)
    {
        if (geometry is null || geometry.Layers.Count == 0)
        {
            return null;
        }

        int length = HeaderSize;
        var layerNames = new List<byte[]>(geometry.Layers.Count);
        foreach (WorldTileLayerGeometryDto layer in geometry.Layers)
        {
            byte[] layerName = string.IsNullOrWhiteSpace(layer.LayerDefName)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(layer.LayerDefName!);
            layerNames.Add(layerName);
            checked
            {
                length += LayerHeaderSize + layerName.Length + layer.TileCenters.Count * TileCenterSize;
            }
        }

        using var stream = new MemoryStream(length);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(geometry.Layers.Count);
            for (int layerIndex = 0; layerIndex < geometry.Layers.Count; layerIndex++)
            {
                WorldTileLayerGeometryDto layer = geometry.Layers[layerIndex];
                byte[] layerName = layerNames[layerIndex];
                writer.Write(layer.LayerId);
                writer.Write(layer.AverageTileSize);
                writer.Write(layer.TileCenters.Count);
                writer.Write(layerName.Length);
                writer.Write(layerName);
                foreach (WorldTileCenterDto center in layer.TileCenters.OrderBy(center => center.Tile))
                {
                    writer.Write(center.Tile);
                    writer.Write(center.X);
                    writer.Write(center.Y);
                    writer.Write(center.Z);
                }
            }
        }

        return stream.ToArray();
    }

    public static WorldTileGeometryDto? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < HeaderSize)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
            {
                return null;
            }

            int layerCount = reader.ReadInt32();
            if (layerCount <= 0)
            {
                return null;
            }

            var layers = new List<WorldTileLayerGeometryDto>(layerCount);
            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                int layerId = reader.ReadInt32();
                float averageTileSize = reader.ReadSingle();
                int tileCenterCount = reader.ReadInt32();
                int nameLength = reader.ReadInt32();
                if (tileCenterCount < 0 || nameLength < 0 || nameLength > stream.Length - stream.Position
                    || tileCenterCount > (stream.Length - stream.Position - nameLength) / TileCenterSize)
                {
                    return null;
                }

                string? layerDefName = nameLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                var centers = new List<WorldTileCenterDto>(tileCenterCount);
                for (int tileIndex = 0; tileIndex < tileCenterCount; tileIndex++)
                {
                    centers.Add(new WorldTileCenterDto(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                }

                layers.Add(new WorldTileLayerGeometryDto(layerId, layerDefName, averageTileSize, centers));
            }

            return stream.Position == stream.Length ? new WorldTileGeometryDto(layers) : null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
