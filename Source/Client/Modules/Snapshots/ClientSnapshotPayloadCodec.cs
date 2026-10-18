using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal static class ClientSnapshotPayloadCodec
{
    internal static byte[] Encode(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    internal static byte[] Decode(byte[] bytes, string? encoding)
    {
        if (string.Equals(encoding, "RawRws", StringComparison.OrdinalIgnoreCase)) return bytes;
        if (!string.Equals(encoding, "GzipRws", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException(encoding);
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    internal static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    internal static bool HashMatches(byte[] bytes, string? expected) => !string.IsNullOrWhiteSpace(expected)
        && string.Equals(Hash(bytes), expected, StringComparison.OrdinalIgnoreCase);
}
