using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AIRsLight.ClashOfRim.Protocol;

/// <summary>
/// Binary transport codec for <see cref="WorldSubstratePackage"/>.
/// The outer gzip layer avoids JSON/base64 expansion while retaining RimWorld's native tile arrays.
/// </summary>
public static class WorldSubstratePackageCodec
{
    private const uint Magic = 0x53524F43; // CORS
    private const int MaxCompressedBytes = 32 * 1024 * 1024;
    private const int MaxUncompressedBytes = 96 * 1024 * 1024;

    public static byte[] Encode(WorldSubstratePackage package)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        using var uncompressed = new MemoryStream();
        using (var writer = new BinaryWriter(uncompressed, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(WorldSubstratePackage.CurrentFormatVersion);
            writer.Write(package.PersistentRandomValue);
            WriteString(writer, package.GridXml);
            WriteString(writer, package.FeaturesXml);
            WriteString(writer, package.LandmarksXml);
            WriteBytes(writer, package.TileGeometryPayload);
        }

        uncompressed.Position = 0;
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            uncompressed.CopyTo(gzip);
        }

        return compressed.ToArray();
    }

    public static bool TryDecode(byte[] payload, out WorldSubstratePackage? package, out string? failure)
    {
        package = null;
        failure = null;
        if (payload is null || payload.Length == 0)
        {
            failure = "World substrate payload is empty.";
            return false;
        }

        if (payload.Length > MaxCompressedBytes)
        {
            failure = "World substrate payload exceeds the compressed size limit.";
            return false;
        }

        try
        {
            using var compressed = new MemoryStream(payload, writable: false);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var uncompressed = new MemoryStream();
            CopyWithLimit(gzip, uncompressed, MaxUncompressedBytes);
            uncompressed.Position = 0;
            using var reader = new BinaryReader(uncompressed, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic)
            {
                failure = "World substrate payload has an invalid header.";
                return false;
            }

            if (reader.ReadInt32() != WorldSubstratePackage.CurrentFormatVersion)
            {
                failure = "World substrate payload has an unsupported version.";
                return false;
            }

            int persistentRandomValue = reader.ReadInt32();
            string gridXml = ReadString(reader);
            string featuresXml = ReadString(reader);
            string landmarksXml = ReadString(reader);
            byte[] tileGeometryPayload = ReadBytes(reader);
            if (uncompressed.Position != uncompressed.Length)
            {
                failure = "World substrate payload contains trailing bytes.";
                return false;
            }

            package = new WorldSubstratePackage(
                persistentRandomValue,
                gridXml,
                featuresXml,
                landmarksXml,
                tileGeometryPayload);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            failure = "World substrate payload could not be decoded: " + ex.Message;
            return false;
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxUncompressedBytes)
        {
            throw new InvalidDataException("World substrate string length is invalid.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxUncompressedBytes)
        {
            throw new InvalidDataException("World substrate binary segment length is invalid.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return bytes;
    }

    private static void CopyWithLimit(Stream source, Stream destination, int limit)
    {
        var buffer = new byte[81920];
        int total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            checked
            {
                total += read;
            }

            if (total > limit)
            {
                throw new InvalidDataException("World substrate payload exceeds the uncompressed size limit.");
            }

            destination.Write(buffer, 0, read);
        }
    }
}
