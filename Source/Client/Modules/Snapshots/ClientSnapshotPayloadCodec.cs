using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal static class ClientSnapshotPayloadCodec
{
    internal static bool TryDecode(ModSnapshotPackageMetadataDto package, byte[] payload,
        out byte[] original, out ClientSnapshotRestoreFailure? failure)
    {
        original = Array.Empty<byte>();
        failure = null;
        if (package.PayloadBytes > 0 && payload.LongLength != package.PayloadBytes)
            failure = new(ClientSnapshotRestoreFailureKind.PayloadSizeMismatch);
        else if (!HashMatches(payload, package.PayloadSha256))
            failure = new(ClientSnapshotRestoreFailureKind.PayloadHashMismatch);
        else
        {
            try { original = Decode(payload, package.PayloadEncoding); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                failure = ex is NotSupportedException
                    ? new(ClientSnapshotRestoreFailureKind.UnsupportedEncoding, package.PayloadEncoding)
                    : new(ClientSnapshotRestoreFailureKind.DecodeFailed, ex.Message);
                return false;
            }
            if (package.OriginalSaveBytes > 0 && original.LongLength != package.OriginalSaveBytes)
                failure = new(ClientSnapshotRestoreFailureKind.OriginalSizeMismatch);
            else if (!HashMatches(original, package.OriginalSha256))
                failure = new(ClientSnapshotRestoreFailureKind.OriginalHashMismatch);
        }
        return failure is null;
    }

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
