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

    void DeleteUnreferenced(IReadOnlySet<string> referencedArtifactIds);
}

/// <summary>Prepares bytes without publishing them; callers revalidate their business state before publishing.</summary>
public interface IPreparedSnapshotPostUploadArtifactStore : ISnapshotPostUploadArtifactStore
{
    IPreparedSnapshotPostUploadArtifact Prepare(string artifactId, SaveSnapshotPackage package);
}

public interface IPreparedSnapshotPostUploadArtifact : IDisposable
{
    // False means this ID changed after capture; dispose and prepare again outside business locks.
    bool TryPublish();
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
            if (packages.TryGetValue(artifactId, out SaveSnapshotPackage? existing))
            {
                EnsureSameArtifact(artifactId, existing, package);
                return;
            }

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

    public void DeleteUnreferenced(IReadOnlySet<string> referencedArtifactIds)
    {
        ArgumentNullException.ThrowIfNull(referencedArtifactIds);
        lock (gate)
        {
            foreach (string artifactId in packages.Keys.Where(id => !referencedArtifactIds.Contains(id)).ToList())
            {
                packages.Remove(artifactId);
            }
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

    internal static void EnsureSameArtifact(
        string artifactId,
        SaveSnapshotPackage existing,
        SaveSnapshotPackage candidate)
    {
        if (!Equals(existing.Envelope.Identity, candidate.Envelope.Identity)
            || !string.Equals(existing.Envelope.PayloadSha256, candidate.Envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase)
            || !existing.Payload.AsSpan().SequenceEqual(candidate.Payload))
        {
            throw new InvalidOperationException($"Snapshot post-upload artifact '{artifactId}' is immutable and already contains different data.");
        }
    }
}

public sealed class FileSnapshotPostUploadArtifactStore : IPreparedSnapshotPostUploadArtifactStore
{
    private readonly object gate = new();
    private readonly string rootDirectory;

    public FileSnapshotPostUploadArtifactStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(this.rootDirectory);
    }

    // Entries exist only while a preparation is alive; deleted artifact IDs do not accumulate tombstones.
    private readonly Dictionary<string, WriteRevision> activePreparations = new(StringComparer.OrdinalIgnoreCase);
    private sealed class WriteRevision
    {
        public int Users;
        public object Token = new();
    }

    public void Store(string artifactId, SaveSnapshotPackage package)
    {
        while (true)
        {
            using var prepared = Prepare(artifactId, package);
            if (prepared.TryPublish()) return;
        }
    }

    public IPreparedSnapshotPostUploadArtifact Prepare(string artifactId, SaveSnapshotPackage package)
    {
        InMemorySnapshotPostUploadArtifactStore.ValidateArtifactId(artifactId);
        ArgumentNullException.ThrowIfNull(package);
        string path = PathFor(artifactId);
        WriteRevision revision;
        object token;
        byte[]? captured;
        lock (gate)
        {
            if (!activePreparations.TryGetValue(path, out revision!)) activePreparations.Add(path, revision = new());
            revision.Users++;
            token = revision.Token;
            try { captured = Capture(path); }
            catch { Release(path, revision); throw; }
        }
        SaveSnapshotPackageFileWriter.PreparedFile? file = null;
        try
        {
            // Own the input used for comparison and file serialization, even if the caller later changes its array.
            var candidate = package with { Payload = package.Payload.ToArray() };
            var existing = Decode(captured);
            bool conflict = existing is not null && (!Equals(existing.Envelope.Identity, candidate.Envelope.Identity)
                || !string.Equals(existing.Envelope.PayloadSha256, candidate.Envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase)
                || !existing.Payload.AsSpan().SequenceEqual(candidate.Payload));
            if (existing is null)
            {
                var metadata = new PersistedSaveSnapshotPackage(candidate.Envelope.Identity, candidate.Envelope,
                    LightweightIndex(candidate.Index), candidate.Envelope.CreatedAtUtc);
                file = SaveSnapshotPackageFileWriter.Prepare(path, metadata, candidate.Payload);
            }
            return new Preparation(this, artifactId, path, revision, token, conflict, file);
        }
        catch
        {
            file?.Dispose();
            lock (gate) Release(path, revision);
            throw;
        }
    }

    public SaveSnapshotPackage? Read(string artifactId)
    {
        InMemorySnapshotPostUploadArtifactStore.ValidateArtifactId(artifactId);
        byte[]? captured;
        // Close the file before decoding, so Windows deletion/recreation is not blocked by a reader.
        lock (gate) captured = Capture(PathFor(artifactId));
        return Decode(captured);
    }

    private static byte[]? Capture(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (IOException) { return null; }
    }

    private static SaveSnapshotPackage? Decode(byte[]? bytes)
    {
        if (bytes is null) return null;
        using var stream = new MemoryStream(bytes, writable: false);
        return SaveSnapshotPackageFileReader.ReadPackage(stream)?.Package;
    }

    // Called only under gate, including when a successful delete found no existing file.
    private void Changed(string path)
    {
        if (activePreparations.TryGetValue(path, out var revision)) revision.Token = new();
    }

    private void Release(string path, WriteRevision revision)
    {
        if (--revision.Users == 0) activePreparations.Remove(path);
    }

    private sealed class Preparation(FileSnapshotPostUploadArtifactStore owner, string id, string path,
        WriteRevision revision, object token, bool conflict, SaveSnapshotPackageFileWriter.PreparedFile? file)
        : IPreparedSnapshotPostUploadArtifact
    {
        private bool disposed, published;
        public bool TryPublish()
        {
            lock (owner.gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (published) throw new InvalidOperationException("Artifact preparation has already been published.");
                if (!ReferenceEquals(revision.Token, token)) return false;
                if (conflict) throw new InvalidOperationException($"Snapshot post-upload artifact '{id}' is immutable and already contains different data.");
                if (file is not null)
                {
                    file.Publish();
                    owner.Changed(path);
                }
                published = true;
                return true;
            }
        }
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (disposed) return;
                try { file?.Dispose(); }
                finally { owner.Release(path, revision); disposed = true; }
            }
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
            Changed(path);
        }
    }

    public void DeleteUnreferenced(IReadOnlySet<string> referencedArtifactIds)
    {
        ArgumentNullException.ThrowIfNull(referencedArtifactIds);
        lock (gate)
        {
            HashSet<string> referencedPaths = referencedArtifactIds
                .Select(PathFor)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(
                rootDirectory,
                "*" + SaveSnapshotPackageFileReader.PackageExtension,
                SearchOption.TopDirectoryOnly))
            {
                if (!referencedPaths.Contains(Path.GetFullPath(path)))
                {
                    File.Delete(path);
                    Changed(path);
                }
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
