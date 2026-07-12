using System.Security.Cryptography;
using System.Text;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public interface ISnapshotPostUploadArtifactStore
{
    void Store(string artifactId, SaveSnapshotPackage package);

    SaveSnapshotPackage? Read(string artifactId);

    bool Exists(string artifactId);

    void Delete(string artifactId);
}

public sealed class InMemorySnapshotPostUploadArtifactStore : ISnapshotPostUploadArtifactStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, SaveSnapshotPackage> packages = new(StringComparer.Ordinal);

    public void Store(string artifactId, SaveSnapshotPackage package)
    {
        ValidateArtifactId(artifactId);
        ArgumentNullException.ThrowIfNull(package);
        lock (gate)
        {
            packages[artifactId] = package with { Payload = package.Payload.ToArray() };
        }
    }

    public SaveSnapshotPackage? Read(string artifactId)
    {
        ValidateArtifactId(artifactId);
        lock (gate)
        {
            return packages.TryGetValue(artifactId, out SaveSnapshotPackage? package)
                ? package with { Payload = package.Payload.ToArray() }
                : null;
        }
    }

    public bool Exists(string artifactId)
    {
        ValidateArtifactId(artifactId);
        lock (gate)
        {
            return packages.ContainsKey(artifactId);
        }
    }

    public void Delete(string artifactId)
    {
        ValidateArtifactId(artifactId);
        lock (gate)
        {
            packages.Remove(artifactId);
        }
    }

    internal static void ValidateArtifactId(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        if (artifactId.Contains('/') || artifactId.Contains('\\'))
        {
            throw new ArgumentException("Snapshot post-upload artifact IDs cannot contain path separators.", nameof(artifactId));
        }
    }
}

public sealed class FileSnapshotPostUploadArtifactStore : ISnapshotPostUploadArtifactStore
{
    private readonly object gate = new();
    private readonly string rootDirectory;

    public FileSnapshotPostUploadArtifactStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(this.rootDirectory);
    }

    public void Store(string artifactId, SaveSnapshotPackage package)
    {
        InMemorySnapshotPostUploadArtifactStore.ValidateArtifactId(artifactId);
        ArgumentNullException.ThrowIfNull(package);

        var metadata = new PersistedSaveSnapshotPackage(
            package.Envelope.Identity,
            package.Envelope,
            LightweightIndex(package.Index),
            package.Envelope.CreatedAtUtc);
        lock (gate)
        {
            SaveSnapshotPackageFileWriter.WriteAtomically(PathFor(artifactId), metadata, package.Payload);
        }
    }

    public SaveSnapshotPackage? Read(string artifactId)
    {
        InMemorySnapshotPostUploadArtifactStore.ValidateArtifactId(artifactId);
        lock (gate)
        {
            return SaveSnapshotPackageFileReader.ReadPackage(PathFor(artifactId))?.Package;
        }
    }

    public bool Exists(string artifactId)
    {
        InMemorySnapshotPostUploadArtifactStore.ValidateArtifactId(artifactId);
        lock (gate)
        {
            return File.Exists(PathFor(artifactId));
        }
    }

    public void Delete(string artifactId)
    {
        InMemorySnapshotPostUploadArtifactStore.ValidateArtifactId(artifactId);
        lock (gate)
        {
            string path = PathFor(artifactId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string PathFor(string artifactId)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifactId))).ToLowerInvariant();
        return Path.Combine(rootDirectory, hash + SaveSnapshotPackageFileReader.PackageExtension);
    }

    private static SaveSnapshotIndex LightweightIndex(SaveSnapshotIndex index)
    {
        return index with
        {
            SavePath = string.Empty,
            Factions = Array.Empty<FactionSummary>(),
            Extensions = Array.Empty<SaveIndexExtensionData>(),
            WorldObjects = Array.Empty<WorldObjectSummary>(),
            Maps = Array.Empty<MapSummary>(),
            Things = Array.Empty<ThingSummary>(),
            Pawns = Array.Empty<PawnSummary>()
        };
    }
}
