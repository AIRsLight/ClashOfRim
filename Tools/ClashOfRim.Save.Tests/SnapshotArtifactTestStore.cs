using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;

// Capability views are intentional: legacy fixtures must not acquire prepared/versioned behavior.
internal class SnapshotArtifactStoreView(ISnapshotPostUploadArtifactStore inner) : ISnapshotPostUploadArtifactStore
{
    public void Store(string id, SaveSnapshotPackage package) => inner.Store(id, package);
    public SaveSnapshotPackage? Read(string id) => inner.Read(id);
    public bool Exists(string id) => inner.Exists(id);
    public void Delete(string id) => inner.Delete(id);
    public void DeleteUnreferenced(IReadOnlySet<string> ids) => inner.DeleteUnreferenced(ids);
}

internal class VersionedSnapshotArtifactTestStore(FileSnapshotPostUploadArtifactStore inner)
    : SnapshotArtifactStoreView(inner), IVersionedSnapshotPostUploadArtifactStore
{
    public FileSnapshotPostUploadArtifactStore Inner { get; } = inner;
    public int ActiveHandles => ((System.Collections.IDictionary)typeof(FileSnapshotPostUploadArtifactStore)
        .GetField("activePreparations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Inner)!).Count;
    public Action? BeforeRead, AfterRead;
    public ISnapshotPostUploadArtifactRead ReadCaptured(string id)
    {
        BeforeRead?.Invoke();
        var handle = Inner.ReadCaptured(id);
        try { AfterRead?.Invoke(); return handle; }
        catch { handle.Dispose(); throw; }
    }
}

internal sealed class SnapshotArtifactTestStore(string root)
    : VersionedSnapshotArtifactTestStore(new FileSnapshotPostUploadArtifactStore(root)), IPreparedSnapshotPostUploadArtifactStore
{
    public Action? AfterPrepare;
    public IPreparedSnapshotPostUploadArtifact Prepare(string id, SaveSnapshotPackage package)
    {
        var handle = Inner.Prepare(id, package);
        try { AfterPrepare?.Invoke(); return handle; }
        catch { handle.Dispose(); throw; }
    }
}
