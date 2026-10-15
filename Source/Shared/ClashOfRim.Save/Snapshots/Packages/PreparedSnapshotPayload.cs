using System.IO.Compression;
using System.Security.Cryptography;

namespace AIRsLight.ClashOfRim.Save;

/// <summary>Transient, immutable storage preparation; never part of a snapshot or recovery journal.</summary>
public sealed class PreparedSnapshotPayload
{
    private readonly byte[] compressed;
    private readonly byte[] hash;
    private readonly int length;

    private PreparedSnapshotPayload(byte[] compressed, byte[] hash, int length)
    {
        this.compressed = compressed;
        this.hash = hash;
        this.length = length;
    }

    public static PreparedSnapshotPayload Create(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Own the input while preparing, so the compressed member and its fingerprint agree.
        byte[] source = (byte[])payload.Clone();
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(source);
        return new PreparedSnapshotPayload(output.ToArray(), SHA256.HashData(source), source.Length);
    }

    public void Validate(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length != length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("Prepared storage payload does not match the snapshot bytes.");
    }

    internal void WriteTo(Stream destination) => destination.Write(compressed);
}
