using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace AIRsLight.ClashOfRim.Save;

public static class SaveSnapshotPackageBuilder
{
    public const string CurrentPackageVersion = "clash-of-rim-snapshot-v1";

    public static SaveSnapshotPackage FromFile(
        string path,
        SnapshotIdentity identity,
        DateTimeOffset createdAtUtc,
        SnapshotPayloadEncoding payloadEncoding = SnapshotPayloadEncoding.GzipRws)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(identity);

        byte[] originalPayload = File.ReadAllBytes(path);
        byte[] encodedPayload = payloadEncoding switch
        {
            SnapshotPayloadEncoding.RawRws => originalPayload,
            SnapshotPayloadEncoding.GzipRws => Gzip(originalPayload),
            _ => throw new ArgumentOutOfRangeException(nameof(payloadEncoding), payloadEncoding, "Unsupported snapshot payload encoding.")
        };

        SaveSnapshotIndex index = RimWorldSaveIndexReader.Read(path, new SaveIndexReadOptions
        {
            Identity = identity
        });

        var envelope = new SaveSnapshotEnvelope(
            CurrentPackageVersion,
            identity,
            createdAtUtc,
            Path.GetFileName(path),
            index.Meta.GameVersion,
            payloadEncoding,
            originalPayload.LongLength,
            encodedPayload.LongLength,
            Sha256Hex(originalPayload),
            Sha256Hex(encodedPayload),
            GameTicks: ReadGameTicks(path));

        return new SaveSnapshotPackage(envelope, encodedPayload, index);
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var target = new MemoryStream();
        using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return target.ToArray();
    }

    private static string Sha256Hex(byte[] payload)
    {
        byte[] hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static long? ReadGameTicks(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using XmlReader reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.Name == "ticksGame"
                && long.TryParse(reader.ReadElementContentAsString().Trim(), out long ticks))
            {
                return ticks;
            }
        }

        return null;
    }
}
