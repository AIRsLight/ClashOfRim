using System.Security.Cryptography;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Save;

internal static class FileSnapshotConcurrencyTests
{
    private static readonly PausingExtension Extension = new();

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SaveIndexExtensionRegistry.Register(Extension);
        string root = Path.Combine(Path.GetTempPath(), "cor-file-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (SnapshotPayloadEncoding encoding in new[] { SnapshotPayloadEncoding.RawRws, SnapshotPayloadEncoding.GzipRws })
            {
                VerifyPausedRead(root, fixture, encoding);
                foreach (bool metadataOnly in new[] { false, true })
                    foreach (string mutation in new[] { "replace", "delete", "recreate", "unrelated" })
                        VerifyPausedWrite(root, fixture, encoding, metadataOnly, mutation);
            }
            VerifyFailedPreparation(root, fixture);
            VerifyConcurrentHistoryAndReaders(root, fixture);
            VerifyMissingPayload(root, fixture);
            if (OperatingSystem.IsWindows()) VerifyFailedFileMutation(root, fixture);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static void VerifyPausedWrite(string root, Func<string, SaveSnapshotPackage> fixture,
        SnapshotPayloadEncoding encoding, bool metadataOnly, string mutation, bool preparePayload = false)
    {
        string directory = Path.Combine(root, $"write-{encoding}-{metadataOnly}-{mutation}");
        var store = new FileColonySnapshotIndexStore(directory);
        SaveSnapshotPackage before = Package(root, fixture, "write-before", encoding);
        SaveSnapshotPackage pending = Package(root, fixture, "write-pending", encoding);
        SaveSnapshotPackage competing = Package(root, fixture, "write-competing", encoding);
        SaveSnapshotPackage unrelated = Package(root, fixture, "write-unrelated", encoding, "other-user", "other-colony");
        Store(store, before);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var factions = new ObservedList<FactionSummary>(pending.Index.Factions, () =>
        {
            entered.Set();
            Require(release.Wait(TimeSpan.FromSeconds(30)), "writer preparation was not released");
        });
        SaveSnapshotIndex index = pending.Index with { Factions = factions };
        Task write = Task.Run(() =>
        {
            if (metadataOnly)
                store.StoreLatest(new LatestSnapshotRecord(pending.Envelope.Identity, pending.Envelope, index, DateTimeOffset.UtcNow));
            else if (preparePayload) store.StorePrepared(pending, index, DateTimeOffset.UtcNow, store.PreparePayload(pending.Payload));
            else store.StoreLatest(pending, index, DateTimeOffset.UtcNow);
        });
        Task? mutate = null;
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(10)), "writer did not reach serialization");
            mutate = Task.Run(() =>
            {
                Require(store.GetLatest("userA", "colonyA")?.Identity == before.Envelope.Identity,
                    "preparation published metadata before the file");
                VerifyPackage(store.GetLatestPackage("userA", "colonyA")!);
                Require(new FileColonySnapshotIndexStore(directory).GetLatest("userA", "colonyA")?.Identity == before.Envelope.Identity,
                    "reopen loaded an uncommitted temporary package");
                Store(store, unrelated);
                if (mutation is "delete" or "recreate") Require(store.RemoveLatest("userA", "colonyA"), "delete failed during preparation");
                if (mutation is "replace" or "recreate") Store(store, competing);
            });
            Require(mutate.Wait(TimeSpan.FromSeconds(10)), "package preparation held the shared storage lock");
        }
        finally
        {
            release.Set();
            Task.WaitAll(mutate is null ? new[] { write } : new[] { write, mutate }, TimeSpan.FromSeconds(30));
        }
        write.GetAwaiter().GetResult();
        Require(factions.Enumerations == (mutation == "unrelated" ? 1 : 2), "candidate was not retried only for the same colony revision");
        foreach (var current in new[] { store, new FileColonySnapshotIndexStore(directory) })
        {
            Require(current.GetLatest("userA", "colonyA")?.Identity == pending.Envelope.Identity, "prepared write did not publish its coherent version");
            Require(current.HasAcceptedOriginalHash("userA", "colonyA", before.Envelope.OriginalSha256) == (mutation is "replace" or "unrelated"),
                "preparation restored deleted history or lost earlier history");
            Require(current.HasAcceptedOriginalHash("userA", "colonyA", competing.Envelope.OriginalSha256) == (mutation is "replace" or "recreate"),
                "prepared publication lost competing history");
            Require(current.HasAcceptedOriginalHash("userA", "colonyA", pending.Envelope.OriginalSha256), "prepared publication omitted its own history");
            Require(current.GetLatest("other-user", "other-colony")?.Identity == unrelated.Envelope.Identity, "prepared write changed another colony");
            SaveSnapshotPackage? result = current.GetLatestPackage("userA", "colonyA");
            if (metadataOnly) Require(result is null, "metadata-only publication retained an unrelated payload");
            else
            {
                Require(result is not null && result.Payload.SequenceEqual(pending.Payload), "prepared write mixed payload versions");
                VerifyPackage(result!);
            }
        }
        Require(!Directory.EnumerateFiles(Path.Combine(directory, "packages"), "*.tmp").Any(), "discarded candidate leaked a temporary file");
    }

    private static void VerifyFailedPreparation(string root, Func<string, SaveSnapshotPackage> fixture)
    {
        string directory = Path.Combine(root, "prepare-failure");
        var store = new FileColonySnapshotIndexStore(directory);
        SaveSnapshotPackage before = Package(root, fixture, "prepare-before", SnapshotPayloadEncoding.RawRws);
        SaveSnapshotPackage next = Package(root, fixture, "prepare-next", SnapshotPayloadEncoding.RawRws);
        Store(store, before);
        foreach (bool metadataOnly in new[] { false, true })
        {
            var index = next.Index with
            {
                Factions = new ObservedList<FactionSummary>(next.Index.Factions, () => throw new InvalidOperationException("injected preparation failure"))
            };
            bool failed = false;
            try
            {
                if (metadataOnly) store.StoreLatest(new LatestSnapshotRecord(next.Envelope.Identity, next.Envelope, index, DateTimeOffset.UtcNow));
                else store.StoreLatest(next, index, DateTimeOffset.UtcNow);
            }
            catch (InvalidOperationException ex) when (ex.Message == "injected preparation failure") { failed = true; }
            Require(failed, "preparation failure did not propagate");
            foreach (var current in new[] { store, new FileColonySnapshotIndexStore(directory) })
            {
                Require(current.GetLatest("userA", "colonyA")?.Identity == before.Envelope.Identity, "preparation failure changed latest snapshot");
                Require(!current.HasAcceptedOriginalHash("userA", "colonyA", next.Envelope.OriginalSha256), "preparation failure published history");
                VerifyPackage(current.GetLatestPackage("userA", "colonyA")!);
            }
            Require(!Directory.EnumerateFiles(Path.Combine(directory, "packages"), "*.tmp").Any(), "preparation failure leaked temporary file");
        }
        Store(store, next);
        VerifyPackage(store.GetLatestPackage("userA", "colonyA")!);
    }

    private sealed class ObservedList<T>(IReadOnlyList<T> items, Action onFirstEnumeration) : IReadOnlyList<T>
    {
        public int Enumerations { get; private set; }
        public int Count => items.Count;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator()
        {
            if (++Enumerations == 1) onFirstEnumeration();
            return items.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void VerifyPausedRead(string root, Func<string, SaveSnapshotPackage> fixture, SnapshotPayloadEncoding encoding)
    {
        var store = new FileColonySnapshotIndexStore(Path.Combine(root, encoding.ToString()));
        SaveSnapshotPackage before = Package(root, fixture, "paused-" + encoding, encoding);
        SaveSnapshotPackage next = Package(root, fixture, "next-" + encoding, encoding);
        SaveSnapshotPackage recreated = Package(root, fixture, "recreated-" + encoding, encoding);
        SaveSnapshotPackage unrelated = Package(root, fixture, "unrelated-" + encoding, encoding, "other-user", "other-colony");
        Store(store, before);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Extension.Arm(before.Envelope.Identity.SnapshotId!, entered, release);
        Task<SaveSnapshotPackage?> read = Task.Run(() => store.GetLatestPackage("userA", "colonyA"));
        Task? mutate = null;
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(10)), "reader did not reach index extension");
            mutate = Task.Run(() =>
            {
                Store(store, unrelated);
                Require(store.GetLatest("other-user", "other-colony")?.Identity.SnapshotId == unrelated.Envelope.Identity.SnapshotId,
                    "unrelated colony metadata was blocked or lost");
                Store(store, next);
                Require(store.RemoveLatest("userA", "colonyA"), "replacement could not be removed while old reader remained open");
                Store(store, recreated);
            });
            Require(mutate.Wait(TimeSpan.FromSeconds(10)), "index extension held the shared store lock");
        }
        finally
        {
            release.Set();
            Extension.Disarm();
            Task.WaitAll(mutate is null ? new Task[] { read } : new Task[] { read, mutate }, TimeSpan.FromSeconds(10));
        }
        SaveSnapshotPackage result = read.GetAwaiter().GetResult() ?? throw new Exception("Pinned read lost its original file.");
        Require(result.Envelope.Identity == before.Envelope.Identity && result.Payload.SequenceEqual(before.Payload),
            "in-flight read mixed metadata with a replacement payload");
        VerifyPackage(result);
        Require(store.GetLatest("userA", "colonyA")?.Identity == recreated.Envelope.Identity,
            "finishing old read republished a removed snapshot");
        VerifyPackage(store.GetLatestPackage("userA", "colonyA")!);
        var reopened = new FileColonySnapshotIndexStore(Path.Combine(root, encoding.ToString()));
        Require(reopened.GetLatest("userA", "colonyA")?.Identity == recreated.Envelope.Identity, "restart resurrected the old file");
        Require(!reopened.HasAcceptedOriginalHash("userA", "colonyA", before.Envelope.OriginalSha256), "removed colony retained old history");
    }

    private static void VerifyConcurrentHistoryAndReaders(string root, Func<string, SaveSnapshotPackage> fixture)
    {
        string directory = Path.Combine(root, "history");
        var store = new FileColonySnapshotIndexStore(directory);
        SaveSnapshotPackage[] packages = Enumerable.Range(0, 40)
            .Select(index => Package(root, fixture, "history-" + index, SnapshotPayloadEncoding.GzipRws)).ToArray();
        Store(store, packages[0]);
        using var start = new ManualResetEventSlim();
        Task[] writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            start.Wait();
            for (int index = worker; index < packages.Length; index += 4)
            {
                SaveSnapshotPackage package = packages[index];
                if (index % 2 == 0) Store(store, package);
                else store.StoreLatest(new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index, DateTimeOffset.UtcNow));
            }
        })).ToArray();
        Task[] readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int index = 0; index < 50; index++)
            {
                SaveSnapshotPackage? package = store.GetLatestPackage("userA", "colonyA");
                // Metadata-only publications legitimately have no downloadable payload.
                if (package is not null) VerifyPackage(package);
            }
        })).ToArray();
        start.Set();
        Task.WaitAll(writers.Concat(readers).ToArray());
        Store(store, packages[^1]);
        var reopened = new FileColonySnapshotIndexStore(directory);
        foreach (SaveSnapshotPackage package in packages)
        {
            Require(store.HasAcceptedOriginalHash("userA", "colonyA", package.Envelope.OriginalSha256), "concurrent publication lost in-memory replay history");
            Require(reopened.HasAcceptedOriginalHash("userA", "colonyA", package.Envelope.OriginalSha256), "concurrent publication lost durable replay history");
        }
        VerifyPackage(reopened.GetLatestPackage("userA", "colonyA")!);
    }

    private static void VerifyMissingPayload(string root, Func<string, SaveSnapshotPackage> fixture)
    {
        string directory = Path.Combine(root, "missing");
        var store = new FileColonySnapshotIndexStore(directory);
        SaveSnapshotPackage package = Package(root, fixture, "missing", SnapshotPayloadEncoding.GzipRws);
        store.StoreLatest(new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index, DateTimeOffset.UtcNow));
        Require(store.GetLatestPackage("userA", "colonyA") is null, "metadata-only file returned a package");
        Store(store, package);
        string path = Path.Combine(directory, "packages", SaveSnapshotPackageFileReader.PackageFileName("userA", "colonyA"));
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Require(store.GetLatestPackage("userA", "colonyA") is null, "corrupt file returned a package");
        Store(store, package);
        VerifyPackage(store.GetLatestPackage("userA", "colonyA")!);
        File.Delete(path);
        Require(store.GetLatestPackage("userA", "colonyA") is null, "missing file returned a package");
    }

    private static void VerifyFailedFileMutation(string root, Func<string, SaveSnapshotPackage> fixture)
    {
        string directory = Path.Combine(root, "failure");
        var store = new FileColonySnapshotIndexStore(directory);
        SaveSnapshotPackage before = Package(root, fixture, "before-failure", SnapshotPayloadEncoding.GzipRws);
        SaveSnapshotPackage next = Package(root, fixture, "after-failure", SnapshotPayloadEncoding.GzipRws);
        Store(store, before);
        string path = Path.Combine(directory, "packages", SaveSnapshotPackageFileReader.PackageFileName("userA", "colonyA"));
        // Windows sharing semantics let us deterministically fail rename and delete, without corrupting files.
        using (var denied = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (Action mutation in new Action[]
            {
                () => Store(store, next),
                () => store.StoreLatest(new LatestSnapshotRecord(next.Envelope.Identity, next.Envelope, next.Index, DateTimeOffset.UtcNow)),
                () => store.RemoveLatest("userA", "colonyA")
            })
            {
                bool failed = false;
                try { mutation(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Require(failed, "file mutation was not denied");
                Require(store.GetLatest("userA", "colonyA")?.Identity == before.Envelope.Identity, "failed mutation changed latest cache");
                Require(store.HasAcceptedOriginalHash("userA", "colonyA", before.Envelope.OriginalSha256)
                    && !store.HasAcceptedOriginalHash("userA", "colonyA", next.Envelope.OriginalSha256), "failed mutation changed replay history");
                Require(!Directory.EnumerateFiles(Path.Combine(directory, "packages"), "*.tmp").Any(), "failed write leaked temporary file");
            }
        }
        var reopened = new FileColonySnapshotIndexStore(directory);
        Require(reopened.GetLatest("userA", "colonyA")?.Identity == before.Envelope.Identity, "failed mutation changed durable state");
        Store(store, next);
        VerifyPackage(store.GetLatestPackage("userA", "colonyA")!);
        Require(store.RemoveLatest("userA", "colonyA"), "delete retry did not succeed");
    }

    private static SaveSnapshotPackage Package(string root, Func<string, SaveSnapshotPackage> fixture, string id,
        SnapshotPayloadEncoding encoding, string user = "userA", string colony = "colonyA")
    {
        SaveSnapshotPackage template = fixture(id);
        using var xml = new MemoryStream(SaveSnapshotPackageFileReader.DecodePayload(template.Payload, template.Envelope.PayloadEncoding));
        XDocument document = XDocument.Load(xml);
        document.AddFirst(new XComment(id));
        string path = Path.Combine(root, id + ".rws");
        document.Save(path);
        return SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity(user, colony, id), DateTimeOffset.UtcNow, encoding);
    }

    private static void Store(FileColonySnapshotIndexStore store, SaveSnapshotPackage package)
        => store.StoreLatest(package, package.Index, DateTimeOffset.UtcNow);

    private static void VerifyPackage(SaveSnapshotPackage package)
    {
        Require(string.Equals(Convert.ToHexString(SHA256.HashData(package.Payload)), package.Envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase), "payload and metadata hash disagree");
        byte[] original = SaveSnapshotPackageFileReader.DecodePayload(package.Payload, package.Envelope.PayloadEncoding);
        Require(string.Equals(Convert.ToHexString(SHA256.HashData(original)), package.Envelope.OriginalSha256, StringComparison.OrdinalIgnoreCase), "XML and metadata hash disagree");
        Require(package.Index.Things.Any(thing => thing.GlobalKey.Contains("snapshot:" + package.Envelope.Identity.SnapshotId, StringComparison.Ordinal)), "rebuilt index used another snapshot identity");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("File snapshot concurrency: " + message);
    }

    private sealed class PausingExtension : ISaveIndexExtension
    {
        private string? target;
        private ManualResetEventSlim? entered, release;
        private int armed;
        public void Arm(string snapshotId, ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            this.entered = entered;
            this.release = release;
            target = snapshotId;
            Volatile.Write(ref armed, 1);
        }
        public void Disarm() => Volatile.Write(ref armed, 0);
        public IEnumerable<ThingSummary> ReadContainedThings(XElement containerThing, ThingSummary container, SaveIndexReadContext context)
        {
            if (context.Identity.SnapshotId == target && Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered!.Set();
                release!.Wait(TimeSpan.FromSeconds(30));
            }
            return Array.Empty<ThingSummary>();
        }
    }
}
