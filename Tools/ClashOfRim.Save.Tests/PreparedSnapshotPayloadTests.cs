using System.IO.Compression;
using System.Security.Cryptography;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class PreparedSnapshotPayloadTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        SnapshotJournalRecoveryPreparationTests.Run(fixture);
        string root = Path.Combine(Path.GetTempPath(), "cor-prepared-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool raw in new[] { false, true }) VerifyFormat(root, fixture, raw);
            foreach (SnapshotPayloadEncoding encoding in new[] { SnapshotPayloadEncoding.RawRws, SnapshotPayloadEncoding.GzipRws })
            foreach (string mutation in new[] { "replace", "delete", "recreate", "unrelated" })
                FileSnapshotConcurrencyTests.VerifyPausedWrite(root, fixture, encoding, false, mutation, preparePayload: true);
            foreach (string mode in new[] { "success", "revoke", "publish" }) VerifyGate(root, fixture, mode);
            VerifyRecovery(root, fixture);
            foreach (bool upload in new[] { false, true })
            foreach (bool badPayload in new[] { false, true }) VerifyIntentPublication(root, fixture, upload, badPayload);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyFormat(string root, Func<string, SaveSnapshotPackage> fixture, bool raw)
    {
        string directory = Path.Combine(root, "format-" + raw);
        var store = new FileColonySnapshotIndexStore(directory);
        var package = fixture("prepared");
        if (raw)
        {
            byte[] bytes = Decode(package.Payload);
            package = package with { Payload = bytes, Envelope = package.Envelope with { PayloadEncoding = SnapshotPayloadEncoding.RawRws,
                PayloadBytes = bytes.Length, PayloadSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } };
        }
        var upload = new SnapshotUploadReceiver(store).Receive(new SnapshotUploadContext("userA", "colonyA", "prepared"), package, DateTimeOffset.UtcNow);
        Check(upload.Accepted && upload.PreparedStoragePayload is not null, "file upload did not retain preparation");
        string path = SaveSnapshotPackageFileReader.FindPackagePath(directory, "userA", "colonyA")!;
        var read = SaveSnapshotPackageFileReader.ReadPackage(path)!;
        Check(read.Package is not null && read.EncodedPayload!.SequenceEqual(package.Payload), "existing package reader cannot read prepared gzip members");
        string legacy = Path.Combine(root, "legacy-" + raw + ".snapshot.gz");
        SaveSnapshotPackageFileWriter.WriteAtomically(legacy, read.Persisted, package.Payload);
        Check(Decode(File.ReadAllBytes(path)).SequenceEqual(Decode(File.ReadAllBytes(legacy))), "prepared storage changed the CORSPKG1 logical bytes");
        var reopened = new FileColonySnapshotIndexStore(directory);
        Check(reopened.GetLatestPackage("userA", "colonyA")!.Payload.SequenceEqual(package.Payload)
            && reopened.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == upload.AcceptedSnapshot!.Envelope.NextLineageToken
            && reopened.HasAcceptedOriginalHash("userA", "colonyA", package.Envelope.OriginalSha256), "prepared publication lost metadata/history after reopen");
        byte[] prior = File.ReadAllBytes(path);
        var altered = package with { Payload = (byte[])package.Payload.Clone() };
        altered.Payload[0] ^= 1;
        bool rejected = false;
        try { store.StorePrepared(altered, package.Index, DateTimeOffset.UtcNow, upload.PreparedStoragePayload!); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && File.ReadAllBytes(path).SequenceEqual(prior) && !Directory.EnumerateFiles(Path.Combine(directory, "packages"), "*.tmp").Any(),
            "mismatched preparation published data or leaked a temp file");
        Check(!System.Text.Json.JsonSerializer.Serialize(upload).Contains("PreparedStoragePayload"), "transient storage preparation leaked into wire data");
    }

    private static void VerifyGate(string root, Func<string, SaveSnapshotPackage> fixture, string mode)
    {
        var store = new ObservedStore(Path.Combine(root, mode));
        var state = new ClashOfRimNetworkState(snapshotStore: store);
        store.Gate = state.RaidSettlementSnapshotMutationGate;
        ShopCommitCrashTests.Seed(state.ServerShop);
        var package = fixture("paid");
        var actor = new ProtocolIdentity("userA", "colonyA", "before");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.BeforePrepare = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(15)), "preparation not released"); };
        var request = Task.Run(() => SnapshotEndpointTestClient.Invoke<PurchaseServerShopListingResponse>("PurchaseServerShopListingWithSnapshot", state,
            new PurchaseServerShopListingWithSnapshotRequest(ShopCommitCrashTests.Request(), ProtocolDtoMapper.ToMetadataDto(package)), package.Payload, actor));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "endpoint did not prepare storage payload");
            bool acquired = Monitor.TryEnter(state.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3));
            Check(acquired, "payload compression holds the global snapshot mutation gate");
            try
            {
                Check(store.ListLatest().Count == 0 && state.ServerShop.FindReceipt("purchase") is null, "preparation published business state");
                if (mode == "revoke") state.LoginSessions.EndUser(actor.UserId);
                if (mode == "publish")
                {
                    var competing = fixture("competing");
                    store.Inner.StoreLatest(competing, competing.Index, DateTimeOffset.UtcNow);
                }
            }
            finally { Monitor.Exit(state.RaidSettlementSnapshotMutationGate); }
        }
        finally { release.Set(); }
        var reply = request.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        Check(store.Preparations == 1 && store.FallbackStores == 0, "network path lost its prepared storage payload");
        if (mode == "success")
            Check(reply.Result.Accepted && store.PreparedStores == 1 && state.ServerShop.Find("listing")!.StockCount == 2
                && store.GetLatestPackage(actor.UserId, actor.ColonyId!)!.Payload.SequenceEqual(package.Payload), "prepared shop commit failed");
        else
            Check(!reply.Result.Accepted && (mode != "revoke" || reply.Result.ErrorCode == ProtocolErrorCode.SessionUnavailable)
                && store.PreparedStores == 0 && state.ServerShop.FindReceipt("purchase") is null && state.ServerShop.Find("listing")!.StockCount == 3
                && store.GetLatest(actor.UserId, actor.ColonyId!)?.Identity.SnapshotId == (mode == "publish" ? "competing" : null),
                $"prepared payload bypassed current snapshot/session validation ({mode}: {reply.Result.ErrorCode})");
    }

    private static void VerifyRecovery(string root, Func<string, SaveSnapshotPackage> fixture)
    {
        string directory = Path.Combine(root, "recovery"), journal = Path.Combine(root, "journal");
        var store = new ObservedStore(directory);
        var package = fixture("borrowed");
        var upload = new SnapshotUploadReceiver(store).Receive(new SnapshotUploadContext("userA", "colonyA", "borrowed"), package, DateTimeOffset.UtcNow, storeAccepted: false);
        var bank = new BankLoanRegistry();
        var loan = bank.PrepareActivatedLoan("borrow", "userA", "colonyA", "borrowed", 100, 0, 1, 1000, DateTimeOffset.UtcNow, "loan");
        var commits = new SnapshotCommitService(journal);
        store.FailPublication = true;
        bool failed = false;
        try { commits.Commit(store, bank, package, upload.AcceptedSnapshot!, loan, null, (_, _) => { }, preparedStoragePayload: upload.PreparedStoragePayload); }
        catch (IOException) { failed = true; }
        string intent = Directory.GetFiles(journal, "*.json").Single();
        Check(failed && store.PreparedStores == 1 && !File.ReadAllText(intent).Contains("PreparedStoragePayload")
            && bank.GetActive("userA", "colonyA") is null && store.ListLatest().Count == 0, "failed prepared publication lost isolation or persisted transient data");
        // A fresh coordinator has only durable raw bytes; recovery must work without the prepared member.
        store = new ObservedStore(directory);
        new SnapshotCommitService(journal).Recover(store, bank, (_, _) => { });
        Check(store.FallbackStores == 1 && store.PreparedStores == 0 && bank.GetActive("userA", "colonyA")?.LoanId == "loan"
            && store.GetLatestPackage("userA", "colonyA")!.Payload.SequenceEqual(package.Payload)
            && !Directory.EnumerateFiles(journal, "*.json").Any(), "recovery depended on transient prepared bytes");
    }

    private static void VerifyIntentPublication(string root, Func<string, SaveSnapshotPackage> fixture, bool upload, bool badPayload)
    {
        string directory = Path.Combine(root, "intent-" + upload + "-" + badPayload), journal = Path.Combine(directory, "journal");
        var store = new FileColonySnapshotIndexStore(Path.Combine(directory, "snapshots"));
        var package = fixture("new-intent");
        var snapshot = new SnapshotUploadReceiver(store).Receive(new SnapshotUploadContext("userA", "colonyA", "new-intent"),
            package, DateTimeOffset.UtcNow, storeAccepted: false).AcceptedSnapshot!;
        var bank = new BankLoanRegistry();
        var contracts = new MercenaryContractRegistry();
        var commits = new SnapshotCommitService(journal);
        var loan = bank.PrepareActivatedLoan("borrow", "userA", "colonyA", "new-intent", 100, 0, 1, 1000, snapshot.AcceptedAtUtc, "loan");
        int calls = 0;
        var payload = store.PreparePayload(badPayload ? [1, 2, 3] : package.Payload);
        void Commit()
        {
            if (upload) commits.CommitUpload(store, bank, contracts, package, snapshot,
                PendingSnapshotOperations.Capture(bank, contracts, snapshot), null, null, null, (_, _, _) => calls++, preparedStoragePayload: payload);
            else commits.Commit(store, bank, package, snapshot, loan, null, (_, _) => calls++, preparedStoragePayload: payload);
        }
        if (!badPayload) { Directory.Delete(journal); File.WriteAllText(journal, "block journal directory"); }
        bool failed = false;
        try { Commit(); } catch (Exception ex) when (badPayload ? ex is InvalidDataException : ex is IOException) { failed = true; }
        Check(failed && !commits.HasPending && !commits.HasPendingUploads && store.ListLatest().Count == 0
            && bank.GetActive("userA", "colonyA") is null && calls == 0,
            "Failed intent validation/persistence published memory, count, snapshot or business state.");
        if (!badPayload) { File.Delete(journal); Directory.CreateDirectory(journal); }
        Check(!Directory.EnumerateFiles(journal, "*.json").Any(), "Rejected intent was journaled.");
        payload = store.PreparePayload(package.Payload);
        Commit();
        Check(!commits.HasPending && !commits.HasPendingUploads && calls == 1
            && store.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "new-intent"
            && (bank.GetActive("userA", "colonyA") is not null) == !upload,
            "Failed registration poisoned a later commit or its upload count.");
    }

    private static byte[] Decode(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); gzip.CopyTo(output); return output.ToArray();
    }

    private sealed class ObservedStore(string directory) : IPreparedColonySnapshotPackageStore, IColonySnapshotHistoryStore
    {
        public readonly FileColonySnapshotIndexStore Inner = new(directory);
        public object? Gate;
        public Action? BeforePrepare;
        public bool FailPublication;
        public int Preparations, PreparedStores, FallbackStores;
        public PreparedSnapshotPayload PreparePayload(byte[] bytes)
        {
            Check(Gate is null || !Monitor.IsEntered(Gate), "preparation ran under the global mutation gate");
            Preparations++; BeforePrepare?.Invoke(); return Inner.PreparePayload(bytes);
        }
        public void StorePrepared(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at, PreparedSnapshotPayload prepared)
        {
            Check(Gate is null || Monitor.IsEntered(Gate), "publication escaped the mutation gate");
            PreparedStores++; if (FailPublication) throw new IOException("injected prepared publication failure");
            Inner.StorePrepared(package, index, at, prepared);
        }
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at) { FallbackStores++; Inner.StoreLatest(package, index, at); }
        public void StoreLatest(LatestSnapshotRecord snapshot) => Inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => Inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => Inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => Inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => Inner.RemoveLatest(owner, colony);
        public bool HasAcceptedOriginalHash(string owner, string colony, string hash) => Inner.HasAcceptedOriginalHash(owner, colony, hash);
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
