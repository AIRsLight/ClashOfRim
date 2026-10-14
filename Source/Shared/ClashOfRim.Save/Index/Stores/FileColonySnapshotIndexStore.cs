using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIRsLight.ClashOfRim.Save;

public sealed class FileColonySnapshotIndexStore : IColonySnapshotPackageStore, IColonySnapshotHistoryStore
{
    private static readonly byte[] SnapshotPackageMagic = Encoding.ASCII.GetBytes("CORSPKG1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string PackageExtension = ".snapshot.gz";

    private readonly object gate = new();
    private readonly string packageDirectory;
    private readonly Dictionary<string, LatestSnapshotRecord> latestByColony = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> packageFileNameByColony = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> acceptedOriginalHashesByColony = new(StringComparer.Ordinal);

    public FileColonySnapshotIndexStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        packageDirectory = Path.Combine(rootDirectory, "packages");
        Directory.CreateDirectory(packageDirectory);
        LoadExistingMetadata();
    }

    public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex rebuiltIndex, DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(rebuiltIndex);

        StoreLatest(new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, rebuiltIndex, acceptedAtUtc), package.Payload);
    }

    public void StoreLatest(LatestSnapshotRecord snapshot)
        => StoreLatest(snapshot, encodedPayload: null);

    private void StoreLatest(LatestSnapshotRecord snapshot, byte[]? encodedPayload)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(snapshot.Identity);

        string key = Key(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
        while (true)
        {
            HashSet<string>? previousHashes;
            lock (gate)
                acceptedOriginalHashesByColony.TryGetValue(key, out previousHashes);

            // Published sets are never mutated. Their reference is also the revision
            // token, so replacement or deletion invalidates an in-flight candidate.
            var acceptedOriginalHashes = previousHashes is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(previousHashes, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(snapshot.Envelope.OriginalSha256))
            {
                acceptedOriginalHashes.Add(snapshot.Envelope.OriginalSha256);
            }
            var persisted = new PersistedSaveSnapshotPackage(
                snapshot.Identity,
                snapshot.Envelope,
                LightweightIndex(snapshot.Index),
                snapshot.AcceptedAtUtc,
                acceptedOriginalHashes.OrderBy(hash => hash, StringComparer.OrdinalIgnoreCase).ToList());

            using var prepared = SaveSnapshotPackageFileWriter.Prepare(PackagePath(key), persisted, encodedPayload);
            lock (gate)
            {
                acceptedOriginalHashesByColony.TryGetValue(key, out HashSet<string>? currentHashes);
                if (!ReferenceEquals(previousHashes, currentHashes)) continue;

                prepared.Publish();
                latestByColony[key] = snapshot;
                packageFileNameByColony[key] = PackageFileName(key);
                acceptedOriginalHashesByColony[key] = acceptedOriginalHashes;
                return;
            }
        }
    }

    public LatestSnapshotRecord? GetLatest(string ownerId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            latestByColony.TryGetValue(Key(ownerId, colonyId), out LatestSnapshotRecord? snapshot);
            return snapshot;
        }
    }

    public IReadOnlyList<LatestSnapshotRecord> ListLatest()
    {
        lock (gate)
        {
            return latestByColony.Values
                .OrderBy(snapshot => snapshot.Identity.OwnerId, StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.Identity.ColonyId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public SaveSnapshotPackage? GetLatestPackage(string ownerId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        LatestSnapshotRecord snapshot;
        byte[] packageBytes;
        lock (gate)
        {
            string key = Key(ownerId, colonyId);
            if (!latestByColony.TryGetValue(key, out snapshot!)
                || !packageFileNameByColony.TryGetValue(key, out string? packageFileName)
                || string.IsNullOrWhiteSpace(packageFileName))
            {
                return null;
            }

            try
            {
                // Capture the matching compressed file while publication is excluded. Do not
                // retain an open handle: Windows delete-pending files can block colony recreation.
                packageBytes = File.ReadAllBytes(Path.Combine(packageDirectory, packageFileName));
            }
            catch (IOException) { return null; }
        }

        using (var source = new MemoryStream(packageBytes, writable: false))
        {
            byte[]? packagePayload = ReadSnapshotPackagePayload(source);
            if (packagePayload is null) return null;
            SaveSnapshotIndex? rebuiltIndex = RebuildIndex(packagePayload, snapshot.Envelope.PayloadEncoding, snapshot.Identity);
            return rebuiltIndex is null ? null : new SaveSnapshotPackage(snapshot.Envelope, packagePayload, rebuiltIndex);
        }
    }

    public bool RemoveLatest(string ownerId, string colonyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        string key = Key(ownerId, colonyId);
        lock (gate)
        {
            bool removed = false;
            string packagePath = PackagePath(key);
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
                removed = true;
            }

            removed |= latestByColony.Remove(key);
            packageFileNameByColony.Remove(key);
            acceptedOriginalHashesByColony.Remove(key);

            return removed;
        }
    }

    public bool HasAcceptedOriginalHash(string ownerId, string colonyId, string originalSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(colonyId);

        lock (gate)
        {
            return !string.IsNullOrWhiteSpace(originalSha256)
                && acceptedOriginalHashesByColony.TryGetValue(Key(ownerId, colonyId), out HashSet<string>? hashes)
                && hashes.Contains(originalSha256);
        }
    }

    private void LoadExistingMetadata()
    {
        foreach (string path in EnumerateSnapshotPackageFiles())
        {
            PersistedSaveSnapshotPackage? persisted = ReadPersisted(path);
            if (persisted is null
                || string.IsNullOrWhiteSpace(persisted.Identity.OwnerId)
                || string.IsNullOrWhiteSpace(persisted.Identity.ColonyId))
            {
                continue;
            }

            string key = LoadPersistedSnapshot(persisted);
            if (HasSnapshotPackagePayload(path))
            {
                packageFileNameByColony[key] = Path.GetFileName(path);
            }
        }
    }

    private string LoadPersistedSnapshot(PersistedSaveSnapshotPackage persisted)
    {
        string key = Key(persisted.Identity.OwnerId!, persisted.Identity.ColonyId!);
        latestByColony[key] = new LatestSnapshotRecord(
            persisted.Identity,
            persisted.Envelope,
            persisted.Index,
            persisted.AcceptedAtUtc);

        var hashes = new HashSet<string>(
            persisted.AcceptedOriginalSha256 ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(persisted.Envelope.OriginalSha256))
        {
            hashes.Add(persisted.Envelope.OriginalSha256);
        }

        acceptedOriginalHashesByColony[key] = hashes;
        return key;
    }

    private static PersistedSaveSnapshotPackage? ReadPersisted(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (path.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                return ReadSnapshotPackageMetadata(stream);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string PackagePath(string key)
    {
        return Path.Combine(packageDirectory, PackageFileName(key));
    }

    private static string PackageFileName(string key)
    {
        return key + PackageExtension;
    }

    private IEnumerable<string> EnumerateSnapshotPackageFiles()
    {
        return Directory.EnumerateFiles(packageDirectory, "*" + PackageExtension, SearchOption.TopDirectoryOnly);
    }

    private static void ValidateIdentity(SnapshotIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.OwnerId)
            || string.IsNullOrWhiteSpace(identity.ColonyId))
        {
            throw new ArgumentException("Snapshot identity must include owner and colony ids.", nameof(identity));
        }
    }

    private static string Key(string ownerId, string colonyId)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ownerId + "\u001f" + colonyId);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static PersistedSaveSnapshotPackage? ReadSnapshotPackageMetadata(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
        if (!ReadAndValidatePackageMagic(reader))
        {
            return null;
        }

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

    private static byte[]? ReadSnapshotPackagePayload(Stream stream)
    {
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
            if (!ReadAndValidatePackageMagic(reader))
            {
                return null;
            }

            long metadataLength = reader.ReadInt64();
            if (metadataLength < 0 || metadataLength > int.MaxValue)
            {
                return null;
            }

            if (reader.ReadBytes((int)metadataLength).Length != metadataLength)
            {
                return null;
            }

            long payloadLength = reader.ReadInt64();
            if (payloadLength < 0 || payloadLength > int.MaxValue)
            {
                return null;
            }

            byte[] payload = reader.ReadBytes((int)payloadLength);
            return payload.Length == payloadLength ? payload : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private static bool HasSnapshotPackagePayload(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
            if (!ReadAndValidatePackageMagic(reader))
            {
                return false;
            }

            long metadataLength = reader.ReadInt64();
            if (metadataLength < 0 || metadataLength > int.MaxValue)
            {
                return false;
            }

            if (reader.ReadBytes((int)metadataLength).Length != metadataLength)
            {
                return false;
            }

            return reader.ReadInt64() >= 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool ReadAndValidatePackageMagic(BinaryReader reader)
    {
        byte[] magic = reader.ReadBytes(SnapshotPackageMagic.Length);
        return magic.SequenceEqual(SnapshotPackageMagic);
    }

    private static SaveSnapshotIndex LightweightIndex(SaveSnapshotIndex index)
    {
        return index with
        {
            SavePath = string.Empty,
            Things = Array.Empty<ThingSummary>()
        };
    }

    private static SaveSnapshotIndex? RebuildIndex(
        byte[] payload,
        SnapshotPayloadEncoding encoding,
        SnapshotIdentity identity)
    {
        byte[] originalPayload;
        try
        {
            originalPayload = DecodePayload(payload, encoding);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"clash-of-rim-snapshot-{Guid.NewGuid():N}.rws");
        try
        {
            File.WriteAllBytes(tempPath, originalPayload);
            return RimWorldSaveIndexReader.Read(tempPath, new SaveIndexReadOptions
            {
                Identity = identity,
                Extensions = SaveIndexExtensionRegistry.Registered
            });
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static byte[] DecodePayload(byte[] payload, SnapshotPayloadEncoding encoding)
    {
        return encoding switch
        {
            SnapshotPayloadEncoding.RawRws => payload,
            SnapshotPayloadEncoding.GzipRws => Gunzip(payload),
            _ => throw new NotSupportedException($"Unsupported snapshot payload encoding: {encoding}.")
        };
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
