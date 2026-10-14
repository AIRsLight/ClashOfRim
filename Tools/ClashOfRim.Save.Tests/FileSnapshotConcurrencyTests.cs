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
                VerifyPausedRead(root, fixture, encoding);
            VerifyConcurrentHistoryAndReaders(root, fixture);
            VerifyMissingPayload(root, fixture);
            if (OperatingSystem.IsWindows()) VerifyFailedFileMutation(root, fixture);
        }
        finally { Directory.Delete(root, recursive: true); }
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
