using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal static class SnapshotArtifactTestStoreTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
        => SqliteTestDirectory.With("cor-artifact-probe-", root =>
        {
            var store = new SnapshotArtifactTestStore(root);
            ISnapshotPostUploadArtifactStore legacy = new SnapshotArtifactStoreView(store.Inner);
            ISnapshotPostUploadArtifactStore versioned = new VersionedSnapshotArtifactTestStore(store.Inner);
            Check(legacy is not IVersionedSnapshotPostUploadArtifactStore && legacy is not IPreparedSnapshotPostUploadArtifactStore
                && versioned is IVersionedSnapshotPostUploadArtifactStore && versioned is not IPreparedSnapshotPostUploadArtifactStore,
                "Artifact test views exposed capabilities absent from the original store contract.");
            var package = factory("artifact");
            store.AfterPrepare = () => throw new InjectedFailure();
            Expect<InjectedFailure>(() => store.Prepare("evidence", package), "Artifact preparation hook did not fail.");
            Check(store.ActiveHandles == 0 && !store.Exists("evidence") && !Directory.EnumerateFiles(root, "*.tmp").Any(),
                "Failed preparation hook leaked a handle, temporary file or publication.");
            store.AfterPrepare = null;
            using (var prepared = store.Prepare("evidence", package))
            {
                Check(prepared.IsCurrent && store.ActiveHandles == 1 && !store.Exists("evidence") && prepared.TryPublish(),
                    "Artifact probe skipped deferred publication or lost the prepared version.");
            }
            Check(store.ActiveHandles == 0 && legacy.Read("evidence")!.Envelope == package.Envelope,
                "Legacy artifact view failed to read the shared publication.");
            store.AfterRead = () => throw new InjectedFailure();
            Expect<InjectedFailure>(() => store.ReadCaptured("evidence"), "Captured read hook did not fail.");
            Check(store.ActiveHandles == 0 && store.Exists("evidence"), "Failed captured read leaked a handle or removed evidence.");
            store.AfterRead = null;
            store.BeforeRead = () => throw new InjectedFailure();
            Expect<InjectedFailure>(() => store.ReadCaptured("evidence"), "Pre-read hook did not fail.");
            Check(store.ActiveHandles == 0, "Pre-read failure captured an artifact handle.");
            store.BeforeRead = null;
            using (var captured = ((IVersionedSnapshotPostUploadArtifactStore)versioned).ReadCaptured("evidence"))
            {
                Check(captured.IsCurrent && captured.Package!.Payload.SequenceEqual(package.Payload) && store.ActiveHandles == 1,
                    "Versioned artifact probe lost its captured package.");
                legacy.Delete("evidence");
                Check(!captured.IsCurrent && captured.Package!.Payload.SequenceEqual(package.Payload), "Legacy deletion did not invalidate the captured version.");
            }
            legacy.Store("kept", package); legacy.Store("orphan", package);
            legacy.DeleteUnreferenced(new HashSet<string> { "kept" });
            Check(store.ActiveHandles == 0 && legacy.Exists("kept") && !legacy.Exists("orphan"),
                "Artifact capability views lost cleanup or handle release.");
        });

    private sealed class InjectedFailure : Exception;
}
