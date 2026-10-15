using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;

internal static class ArtifactStoreConcurrencyTests
{
    private static readonly PausingExtension Extension = new();
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SaveIndexExtensionRegistry.Register(Extension);
        string root = Path.Combine(Path.GetTempPath(), "cor-artifact-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool raw in new[] { false, true })
            {
                VerifyRead(Path.Combine(root, "read-" + raw), fixture, raw);
                VerifyRead(Path.Combine(root, "captured-read-" + raw), fixture, raw, versioned: true);
                foreach (string change in new[] { "unrelated", "delete", "replace", "recreate" })
                    VerifyWrite(Path.Combine(root, "write-" + raw + change), fixture, raw, change);
                VerifyPreparation(Path.Combine(root, "prepare-" + raw), fixture, raw);
            }
            VerifyFailures(Path.Combine(root, "failures"), fixture);
            VerifyConcurrent(Path.Combine(root, "concurrent"), fixture);
        }
        finally { Extension.Disarm(); Directory.Delete(root, true); }
    }

    private static void VerifyRead(string root, Func<string, SaveSnapshotPackage> fixture, bool raw, bool versioned = false)
    {
        var store = new FileSnapshotPostUploadArtifactStore(root);
        var before = Package(fixture, "read-before", raw);
        var after = Package(fixture, "read-after", raw);
        store.Store("id", before);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Extension.Arm(before.Envelope.Identity.SnapshotId!, entered, release);
        var read = Task.Run(() =>
        {
            if (!versioned) return store.Read("id");
            using var captured = store.ReadCaptured("id");
            Check(!captured.IsCurrent, "captured read missed a deletion/recreation during decoding");
            return captured.Package;
        });
        Task? mutation = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "read did not reach index rebuilding");
            mutation = Task.Run(() => { store.Delete("id"); store.Store("id", after); store.Store("other", before); });
            Check(mutation.Wait(TimeSpan.FromSeconds(5)), "artifact decoding holds its storage gate");
        }
        finally { release.Set(); Join(read, mutation); Extension.Disarm(); }
        var captured = read.GetAwaiter().GetResult()!;
        Check(captured.Envelope == before.Envelope && captured.Payload.SequenceEqual(before.Payload)
            && store.Read("id")!.Envelope == after.Envelope, "captured read mixed recreated artifact versions");
        Clean(store, root);
    }

    private static void VerifyWrite(string root, Func<string, SaveSnapshotPackage> fixture, bool raw, string change)
    {
        var store = new FileSnapshotPostUploadArtifactStore(root);
        var candidate = Package(fixture, "write-candidate", raw);
        var other = Package(fixture, "write-other", raw);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var observed = new ObservedList(candidate.Index.Meta.ModIds, () =>
        {
            entered.Set();
            Check(release.Wait(TimeSpan.FromSeconds(20)), "writer was not released");
        });
        candidate = candidate with { Index = candidate.Index with { Meta = candidate.Index.Meta with { ModIds = observed } } };
        Exception? failure = null;
        var write = Task.Run(() => { try { store.Store("id", candidate); } catch (Exception ex) { failure = ex; } });
        Task? mutation = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "write did not reach serialization");
            mutation = Task.Run(() =>
            {
                Check(!store.Exists("id") && new FileSnapshotPostUploadArtifactStore(root).Read("id") is null,
                    "preparation exposed an unpublished artifact");
                store.Store("other", other);
                Check(store.Read("other") is not null, "unrelated artifact read failed");
                if (change == "delete") store.Delete("id");
                if (change == "replace") store.Store("id", other);
                if (change == "recreate") { store.Store("id", candidate with { Index = other.Index }); store.Delete("id"); store.Store("id", other); }
            });
            Check(mutation.Wait(TimeSpan.FromSeconds(5)), "artifact compression holds its storage gate");
        }
        finally { release.Set(); Join(write, mutation); }
        mutation!.GetAwaiter().GetResult();
        Check(change is "replace" or "recreate" ? failure is InvalidOperationException : failure is null,
            "publication did not revalidate immutable content: " + change);
        var expected = change is "replace" or "recreate" ? other : candidate;
        Check(store.Read("id")!.Payload.SequenceEqual(expected.Payload)
            && store.Read("id")!.Envelope == expected.Envelope, "publication overwrote competing artifact content");
        Check(observed.Enumerations == (change == "delete" ? 2 : 1), "unrelated writes invalidated preparation or deletion did not retry");
        Clean(store, root);
    }

    private static void VerifyPreparation(string root, Func<string, SaveSnapshotPackage> fixture, bool raw)
    {
        var store = new FileSnapshotPostUploadArtifactStore(root);
        var first = Package(fixture, "prepared-first", raw);
        var other = Package(fixture, "prepared-other", raw);
        using (var prepared = store.Prepare("id", first))
        {
            Check(!store.Exists("id"), "Prepare published its file");
            store.Store("unrelated", other);
            Check(prepared.TryPublish(), "unrelated ID invalidated preparation");
            Throws<InvalidOperationException>(() => prepared.TryPublish());
        }
        using (var duplicate = store.Prepare("id", first))
        {
            store.DeleteUnreferenced(new HashSet<string> { "unrelated" });
            store.Store("id", other);
            Check(!duplicate.TryPublish(), "stale duplicate accepted a recreated different artifact");
        }
        using (var conflict = store.Prepare("id", first))
        {
            store.Delete("id");
            Check(!conflict.TryPublish(), "stale conflict did not invalidate after deletion");
        }
        using (var discarded = store.Prepare("discarded", first)) { discarded.Dispose(); Throws<ObjectDisposedException>(() => discarded.TryPublish()); }
        Check(!store.Exists("discarded"), "disposed preparation published");
        byte[] original = first.Payload.ToArray();
        using (var owned = store.Prepare("id", first))
        {
            first.Payload[0] ^= 1;
            Check(owned.TryPublish(), "owned preparation failed");
        }
        Check(store.Read("id")!.Payload.SequenceEqual(original), "prepared file followed caller payload mutation");
        Throws<InvalidOperationException>(() => store.Store("id", first)); // Same identity/hash claim, different bytes.
        Check(new FileSnapshotPostUploadArtifactStore(root).Read("id")!.Payload.SequenceEqual(original), "prepared file failed reopening");
        string path = Directory.EnumerateFiles(root, "*.snapshot.gz").First();
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        Check(SaveSnapshotPackageFileReader.ReadPackage(stream) is not null && stream.CanRead, "stream reader changed source ownership");
        using var invalid = new MemoryStream(new byte[] { 0, 1, 2 });
        Check(SaveSnapshotPackageFileReader.ReadPackage(invalid) is null && invalid.CanRead, "invalid stream was closed or accepted");
        using (var captured = store.ReadCaptured("id"))
        {
            store.Store("id", first with { Payload = original });
            Check(captured.IsCurrent, "identical publication invalidated a captured read");
            store.Delete("unrelated");
            Check(captured.IsCurrent, "unrelated deletion invalidated a captured read");
            store.Delete("id");
            store.Store("id", first with { Payload = original });
            Check(!captured.IsCurrent, "same-content recreation resurrected a read version");
            captured.Dispose();
            Throws<ObjectDisposedException>(() => { _ = captured.IsCurrent; });
        }
        using (var missing = store.ReadCaptured("missing"))
        {
            Check(missing.Package is null && missing.IsCurrent, "missing read did not capture absence");
            store.Delete("missing");
            Check(!missing.IsCurrent, "deletion of an absent ID did not invalidate a captured read");
        }
        Clean(store, root);
    }

    private static void VerifyFailures(string root, Func<string, SaveSnapshotPackage> fixture)
    {
        var store = new FileSnapshotPostUploadArtifactStore(root);
        var package = fixture("failure");
        var broken = package with { Index = package.Index with { Meta = package.Index.Meta with
            { ModIds = new ObservedList(package.Index.Meta.ModIds, () => throw new InvalidOperationException("serialization failure")) } } };
        Throws<InvalidOperationException>(() => store.Store("id", broken));
        Check(!store.Exists("id"), "failed preparation published");
        Clean(store, root);
        if (!OperatingSystem.IsWindows()) return;
        store.Store("id", package);
        string path = Directory.EnumerateFiles(root, "*.snapshot.gz").Single();
        store.Delete("id");
        using (var prepared = store.Prepare("id", package))
        using (var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            Throws<UnauthorizedAccessException>(() => prepared.TryPublish());
        Clean(store, root);
        store.Store("id", package);
        Check(store.Read("id")!.Envelope == package.Envelope, "failed publication prevented later retry");
    }

    private static SaveSnapshotPackage Package(Func<string, SaveSnapshotPackage> fixture, string id, bool raw)
    {
        var package = fixture(id);
        if (!raw) return package;
        var bytes = SaveSnapshotPackageFileReader.DecodePayload(package.Payload, package.Envelope.PayloadEncoding);
        return package with { Payload = bytes, Envelope = package.Envelope with { PayloadEncoding = SnapshotPayloadEncoding.RawRws,
            PayloadBytes = bytes.Length, PayloadSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } };
    }
    private static void VerifyConcurrent(string root, Func<string, SaveSnapshotPackage> fixture)
    {
        var store = new FileSnapshotPostUploadArtifactStore(root);
        var same = fixture("same");
        Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.Store("same-id", same)))).GetAwaiter().GetResult();
        int accepted = 0;
        var candidates = Enumerable.Range(0, 12).Select(i => fixture("candidate-" + i)).ToArray();
        Task.WhenAll(candidates.Select(package => Task.Run(() =>
        {
            try { store.Store("competing-id", package); Interlocked.Increment(ref accepted); }
            catch (InvalidOperationException) { }
        }))).GetAwaiter().GetResult();
        Check(accepted == 1 && candidates.Any(package => package.Envelope == store.Read("competing-id")!.Envelope)
            && store.Read("same-id")!.Envelope == same.Envelope, "concurrent immutable publication lost or replaced its winner");
        Clean(store, root);
    }
    private static void Clean(FileSnapshotPostUploadArtifactStore store, string root)
    {
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "preparation leaked a temporary file");
        var revisions = (IDictionary)typeof(FileSnapshotPostUploadArtifactStore).GetField("activePreparations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        Check(revisions.Count == 0, "completed preparations retained revision entries");
    }
    private static void Join(Task worker, Task? mutation)
    {
        try { Task.WaitAll(mutation is null ? [worker] : [worker, mutation], TimeSpan.FromSeconds(25)); }
        catch (AggregateException) { } // Preserve the original boundary assertion; normal paths inspect task results below.
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Artifact concurrency: " + message); }
    private sealed class ObservedList(IReadOnlyList<string> values, Action pause) : IReadOnlyList<string>
    {
        public int Enumerations;
        public int Count => values.Count;
        public string this[int index] => values[index];
        public IEnumerator<string> GetEnumerator() { if (++Enumerations == 1) pause(); return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class PausingExtension : ISaveIndexExtension
    {
        private string? target;
        private ManualResetEventSlim? entered, release;
        private int armed;
        public void Arm(string id, ManualResetEventSlim entered, ManualResetEventSlim release)
        { target = id; this.entered = entered; this.release = release; Volatile.Write(ref armed, 1); }
        public void Disarm() => Volatile.Write(ref armed, 0);
        public IEnumerable<ThingSummary> ReadContainedThings(XElement node, ThingSummary thing, SaveIndexReadContext context)
        {
            if (context.Identity.SnapshotId == target && Interlocked.Exchange(ref armed, 0) == 1)
            { entered!.Set(); Check(release!.Wait(TimeSpan.FromSeconds(20)), "reader was not released"); }
            return [];
        }
    }
}
