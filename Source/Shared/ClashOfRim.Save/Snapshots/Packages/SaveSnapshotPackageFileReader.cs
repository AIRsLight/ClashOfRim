using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Save;

public sealed record PersistedSaveSnapshotPackage(
    SnapshotIdentity Identity,
    SaveSnapshotEnvelope Envelope,
    SaveSnapshotIndex Index,
    DateTimeOffset AcceptedAtUtc,
    IReadOnlyList<string>? AcceptedOriginalSha256 = null);

public sealed record SaveSnapshotPackageFileReadOptions
{
    public bool RebuildIndex { get; init; } = true;

    public SnapshotIdentity? IdentityOverride { get; init; }

    public IReadOnlyList<ISaveIndexExtension>? Extensions { get; init; }
        = SaveIndexExtensionRegistry.Registered;
}

public sealed record SaveSnapshotPackageFileReadResult(
    PersistedSaveSnapshotPackage Persisted,
    byte[]? EncodedPayload,
    SaveSnapshotIndex? RebuiltIndex)
{
    public SaveSnapshotPackage? Package =>
        EncodedPayload is not null && RebuiltIndex is not null
            ? new SaveSnapshotPackage(Persisted.Envelope, EncodedPayload, RebuiltIndex)
            : null;
}

public static class SaveSnapshotPackageFileReader
{
    public const string PackageExtension = ".snapshot.gz";

    private static readonly byte[] SnapshotPackageMagic = Encoding.ASCII.GetBytes("CORSPKG1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string PackageKey(string ownerId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        byte[] bytes = Encoding.UTF8.GetBytes(ownerId + "\u001f" + colonyId);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string PackageFileName(string ownerId, string colonyId)
    {
        return PackageKey(ownerId, colonyId) + PackageExtension;
    }

    public static string? FindPackagePath(string rootDirectory, string ownerId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        string fileName = PackageFileName(ownerId, colonyId);
        var candidates = new[]
        {
            Path.Combine(rootDirectory, fileName),
            Path.Combine(rootDirectory, "packages", fileName),
            Path.Combine(rootDirectory, "snapshots", "packages", fileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static PersistedSaveSnapshotPackage? ReadMetadata(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
            if (!ReadAndValidatePackageMagic(reader))
            {
                return null;
            }

            return ReadPersistedMetadata(reader);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    public static SaveSnapshotPackageFileReadResult? ReadPackage(
        string path,
        SaveSnapshotPackageFileReadOptions? options = null)
    {
        options ??= new SaveSnapshotPackageFileReadOptions();

        try
        {
            using FileStream stream = File.OpenRead(path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
            if (!ReadAndValidatePackageMagic(reader))
            {
                return null;
            }

            PersistedSaveSnapshotPackage? persisted = ReadPersistedMetadata(reader);
            if (persisted is null)
            {
                return null;
            }

            byte[]? encodedPayload = ReadEncodedPayload(reader);
            SaveSnapshotIndex? rebuiltIndex = null;
            if (options.RebuildIndex && encodedPayload is not null)
            {
                SnapshotIdentity identity = options.IdentityOverride ?? persisted.Identity;
                rebuiltIndex = RebuildIndex(encodedPayload, persisted.Envelope.PayloadEncoding, identity, options.Extensions);
            }

            return new SaveSnapshotPackageFileReadResult(persisted, encodedPayload, rebuiltIndex);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    public static byte[]? ReadEncodedPayload(string path)
    {
        return ReadPackage(path, new SaveSnapshotPackageFileReadOptions { RebuildIndex = false })?.EncodedPayload;
    }

    public static bool HasPayload(string path)
    {
        return ReadEncodedPayload(path) is not null;
    }

    public static byte[] DecodePayload(byte[] payload, SnapshotPayloadEncoding encoding)
    {
        return encoding switch
        {
            SnapshotPayloadEncoding.RawRws => payload,
            SnapshotPayloadEncoding.GzipRws => Gunzip(payload),
            _ => throw new NotSupportedException($"Unsupported snapshot payload encoding: {encoding}.")
        };
    }

    private static PersistedSaveSnapshotPackage? ReadPersistedMetadata(BinaryReader reader)
    {
        long metadataLength = reader.ReadInt64();
        if (metadataLength < 0 || metadataLength > int.MaxValue)
        {
            return null;
        }

        byte[] metadataJson = reader.ReadBytes((int)metadataLength);
        if (metadataJson.Length != metadataLength)
        {
            return null;
        }

        return JsonSerializer.Deserialize<PersistedSaveSnapshotPackage>(metadataJson, JsonOptions);
    }

    private static byte[]? ReadEncodedPayload(BinaryReader reader)
    {
        long payloadLength = reader.ReadInt64();
        if (payloadLength < 0)
        {
            return null;
        }

        if (payloadLength > int.MaxValue)
        {
            return null;
        }

        byte[] payload = reader.ReadBytes((int)payloadLength);
        return payload.Length == payloadLength ? payload : null;
    }

    private static bool ReadAndValidatePackageMagic(BinaryReader reader)
    {
        byte[] magic = reader.ReadBytes(SnapshotPackageMagic.Length);
        return magic.SequenceEqual(SnapshotPackageMagic);
    }

    private static SaveSnapshotIndex? RebuildIndex(
        byte[] encodedPayload,
        SnapshotPayloadEncoding encoding,
        SnapshotIdentity identity,
        IReadOnlyList<ISaveIndexExtension>? extensions)
    {
        byte[] originalPayload = DecodePayload(encodedPayload, encoding);
        string tempPath = Path.Combine(Path.GetTempPath(), $"clash-of-rim-snapshot-{Guid.NewGuid():N}.rws");
        try
        {
            File.WriteAllBytes(tempPath, originalPayload);
            return RimWorldSaveIndexReader.Read(tempPath, new SaveIndexReadOptions
            {
                Identity = identity,
                Extensions = extensions
            });
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static byte[] Gunzip(byte[] payload)
    {
        using var source = new MemoryStream(payload);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        gzip.CopyTo(target);
        return target.ToArray();
    }
}
