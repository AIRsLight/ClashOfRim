using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class RaidSettlementCommitTests
{
    public static void Run()
    {
        foreach (string change in new[] { "none", "newer", "token", "removed", "source", "delivery" })
            WithFixture(f => VerifyUnlockedCompression(f, change));
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "before", "after", "player", "insert", "update", "signal", "advanced", "newer", "removed", "hash", "token", "published-hash", "published-payload", "published-token", "source", "collision" })
            WithFixture(f => Recovery(f, fault, reopen));
        WithFixture(VerifyConcurrent);
        foreach (string change in new[] { "hash", "source", "delivery" }) WithFixture(f => VerifyPreparationRace(f, change));
        foreach (bool child in new[] { false, true }) WithFixture(f => VerifyLegacy(f, child));
        foreach (string kind in new[] { "memory", "file", "sqlite" }) WithFixture(f => VerifyBatch(f, kind));
    }

    private static void VerifyUnlockedCompression(Fixture f, string change)
    {
        RaidSettlementOperationResult? result = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => result = RaidSettlementOperationExecutor.Execute(f.State, f.Payload, f.Evidence, f.Now), () =>
            {
                Require(f.Store.Writes == 0 && f.State.Ledger.ListAll().Count == 1
                    && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "Settlement published during preparation.");
                if (change is "newer" or "token")
                {
                    var envelope = change == "newer" ? f.Original.Envelope with { Identity = f.Original.Envelope.Identity with { SnapshotId = "newer" } }
                        : f.Original.Envelope with { NextLineageToken = "replacement-token" };
                    f.Files.StoreLatest(f.Original with { Envelope = envelope }, f.Original.Index, f.Now);
                }
                if (change == "removed") f.Files.RemoveLatest("defender", "defender-colony");
                if (change == "source") f.Sql("update events set target_map_unique_id = 'changed';");
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Raid.EventId, "delivered-during-compression", f.Now);
            });
        bool accepted = change is "none" or "delivery";
        Require(result?.Kind == (accepted ? RaidSettlementOperationResultKind.Completed : RaidSettlementOperationResultKind.ManualReview)
            && f.Store.Preparations == 1 && f.Store.Writes == (accepted ? 1 : 0) && f.Store.PreparedWrites == f.Store.Writes
            && f.State.Ledger.ListAll().Count == (accepted ? 2 : 1) && !Directory.EnumerateFiles(f.Journal, "*.json").Any(),
            "Settlement lost prepared bytes or overwrote competing state: " + change);
        if (change == "delivery") Require(f.State.Ledger.Find(f.Raid.EventId)!.DeliveredToSnapshotId == "delivered-during-compression",
            "Settlement lost concurrent delivery metadata.");
        if (!accepted) Require(f.Files.GetLatest("defender", "defender-colony")?.Identity.SnapshotId ==
            (change == "removed" ? null : change == "newer" ? "newer" : "before")
            && (change != "token" || f.Files.GetLatest("defender", "defender-colony")!.Envelope.NextLineageToken == "replacement-token"),
            "Rejected settlement changed its defender snapshot.");
    }

    private static void Recovery(Fixture f, string fault, bool reopen)
    {
        bool conflict = fault is "newer" or "removed" or "hash" or "token" or "source" or "collision" || fault.StartsWith("published-");
        f.Store.Fault = fault == "after" || fault.StartsWith("published-") ? "after" : fault == "before" || conflict ? "before" : null;
        if (fault == "player") f.Sql("create trigger fail_write before insert on server_players begin select raise(abort, 'player'); end;");
        if (fault == "insert") f.Sql("create trigger fail_write before insert on events when NEW.idempotency_key like 'raid-settlement:%' begin select raise(abort, 'insert'); end;");
        if (fault == "update") f.Sql("create trigger fail_write before update on events begin select raise(abort, 'update'); end;");
        if (fault is "signal" or "advanced")
            Fails(() => f.State.SnapshotCommits.CommitRaidSettlement(f.Store, f.State.BankLoans, f.State.Ledger, f.State.Players,
                f.Edited, f.Mutation, _ => throw new IOException("signal")), "Expected signal interruption.");
        else Fails(() => RaidSettlementOperationExecutor.Execute(f.State, f.Payload, f.Evidence, f.Now), "Expected publication interruption.");
        string path = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        var intent = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(path))!;
        Require(intent.Version == 8 && intent.RaidSettlement is not null, "Settlement intent was not durable.");
        if (fault is "player" or "insert" or "update")
        {
            Require(f.State.Ledger.ListAll().Count == 1 && f.State.Ledger.Find(f.Raid.EventId)!.AppliedSnapshotId is null,
                "Failed player/ledger write exposed a partial settlement batch.");
            f.Sql("drop trigger fail_write;");
        }
        if (fault is "newer" or "hash" or "token" || fault.StartsWith("published-"))
        {
            var package = fault.StartsWith("published-") ? intent.Package : f.Original;
            var envelope = package.Envelope;
            envelope = fault switch
            {
                "newer" => envelope with { Identity = envelope.Identity with { SnapshotId = "newer" } },
                "hash" or "published-hash" => envelope with { OriginalSha256 = "changed" },
                "published-payload" => envelope with { PayloadSha256 = "changed" },
                _ => envelope with { NextLineageToken = "changed" }
            };
            f.Files.StoreLatest(package with { Envelope = envelope }, package.Index, f.Now);
        }
        if (fault == "removed") f.Files.RemoveLatest("defender", "defender-colony");
        if (fault == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Failed);
        if (fault == "collision") f.State.Ledger.Append(f.Mutation.Settlement with { EventId = "collision" });
        AuthoritativeEvent? advanced = null;
        if (fault == "advanced")
        {
            var evt = f.State.Ledger.FindByIdempotencyKey(f.Mutation.Settlement.IdempotencyKey)!;
            f.State.Ledger.MarkDelivered(evt.EventId, "delivered-later", f.Now.AddHours(1));
            advanced = f.State.Ledger.MarkApplied(evt.EventId, "applied-later", f.Now.AddHours(2));
        }
        f.Store.Fault = null;
        if (reopen) f.Open();
        var latest = f.Files.GetLatest("defender", "defender-colony")?.Envelope;
        if (conflict)
        {
            Fails(() => f.State.SnapshotMutations.Recover(), "Conflict was overwritten by recovery.");
            Require(File.Exists(path) && f.Files.GetLatest("defender", "defender-colony")?.Envelope == latest,
                "Recovery discarded its journal or overwrote a conflicting destination.");
            return;
        }
        // Recover through the production coordinator, then retry the operation with a later clock.
        f.State.SnapshotMutations.Recover();
        var duplicate = RaidSettlementOperationExecutor.Execute(f.State, f.Payload, f.Evidence, f.Now.AddHours(4));
        Require(duplicate.Kind == RaidSettlementOperationResultKind.AlreadyCompleted, "Retry recalculated settlement.");
        Require(!Directory.EnumerateFiles(f.Journal, "*.json").Any()
            && f.Files.GetLatestPackage("defender", "defender-colony")!.Payload.SequenceEqual(intent.Package.Payload)
            && f.Files.GetLatest("defender", "defender-colony")!.Envelope == intent.Package.Envelope
            && f.State.Players.FindByUserId("defender")!.CurrentSnapshotId == intent.Package.Envelope.Identity.SnapshotId
            && f.State.Ledger.ListAll().Count == 2
            && f.State.Ledger.Find(f.Raid.EventId)!.LastApplicationResult == EventApplicationResultKind.Applied,
            "Recovery lost or duplicated the fixed snapshot, player reference or event batch.");
        if (advanced is not null) Require(Same(f.State.Ledger.Find(advanced.EventId), advanced), "Recovery reset later notification application.");
        f.Open();
        Require(f.State.Ledger.Find(f.Raid.EventId)!.AppliedSnapshotId == intent.Package.Envelope.Identity.SnapshotId
            && f.State.Players.FindByUserId("defender")!.CurrentSnapshotId == intent.Package.Envelope.Identity.SnapshotId,
            "Completed settlement did not survive reopening.");
    }

    private static void VerifyConcurrent(Fixture f)
    {
        var results = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            RaidSettlementOperationExecutor.Execute(f.State, f.Payload, f.Evidence, f.Now)))).GetAwaiter().GetResult();
        Require(results.Count(r => r.Kind == RaidSettlementOperationResultKind.Completed) == 1
            && results.All(r => r.Kind is RaidSettlementOperationResultKind.Completed or RaidSettlementOperationResultKind.AlreadyCompleted)
            && f.Store.Writes == 1 && f.State.Ledger.ListAll().Count == 2, "Concurrent settlement committed twice.");
    }

    private static void VerifyLegacy(Fixture f, bool child)
    {
        f.State.Ledger.Append(f.Mutation.Settlement);
        if (child) f.Files.StoreLatest(f.Edited, f.Edited.Index, f.Now);
        var original = f.Files.GetLatest("defender", "defender-colony")!.Envelope;
        var result = RaidSettlementOperationExecutor.Execute(f.State, f.Payload, f.Evidence, f.Now);
        Require(result.Kind == RaidSettlementOperationResultKind.ManualReview
            && f.Files.GetLatest("defender", "defender-colony")!.Envelope == original
            && f.State.Ledger.Find(f.Raid.EventId)!.AppliedSnapshotId is null
            && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "Legacy partial settlement was guessed from lineage alone.");
    }

    private static void VerifyPreparationRace(Fixture f, string change)
    {
        f.Store.AfterRead = () =>
        {
            if (change == "hash") f.Files.StoreLatest(f.Original with { Envelope = f.Original.Envelope with { OriginalSha256 = "new-content" } }, f.Original.Index, f.Now);
            if (change == "source") f.Sql("update events set target_map_unique_id = 'other-map';");
            if (change == "delivery") f.State.Ledger.MarkDelivered(f.Raid.EventId, "delivered-during-preparation", f.Now);
        };
        var result = RaidSettlementOperationExecutor.Execute(f.State, f.Payload, f.Evidence, f.Now);
        if (change == "delivery")
            Require(result.Kind == RaidSettlementOperationResultKind.Completed
                && f.State.Ledger.Find(f.Raid.EventId)!.DeliveredToSnapshotId == "delivered-during-preparation", "Concurrent delivery was lost.");
        else Require(result.Kind == RaidSettlementOperationResultKind.ManualReview && f.Store.Writes == 0
            && f.State.Ledger.ListAll().Count == 1 && !Directory.EnumerateFiles(f.Journal, "*.json").Any(),
            "Stale preparation published losses after its snapshot or target changed.");
    }

    private static void VerifyBatch(Fixture f, string kind)
    {
        string path = Path.Combine(f.Root, "ledger.json");
        IAuthoritativeEventLedger ledger = kind switch
        {
            "memory" => new InMemoryAuthoritativeEventLedger(),
            "file" => new FileAuthoritativeEventLedger(path),
            _ => new SqliteAuthoritativeEventLedger(f.Database)
        };
        ledger.Append(f.Raid);
        var batch = f.Mutation.Preflight(ledger);
        if (kind == "file" && OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Fails(() => ledger.ApplyBatch(batch), "Expected file publication failure.");
            Require(ledger.ListAll().Count == 1 && ledger.Find(f.Raid.EventId)!.AppliedSnapshotId is null,
                "Failed file batch leaked into cache.");
        }
        ledger.MarkDelivered(f.Raid.EventId, "existing-delivery", f.Now.AddMinutes(-1));
        Fails(() => ledger.ApplyBatch(batch), "Stale expected event was overwritten.");
        batch = f.Mutation.Preflight(ledger);
        ledger.ApplyBatch(batch);
        ledger.ApplyBatch(batch);
        if (kind == "file") ledger = new FileAuthoritativeEventLedger(path);
        Require(ledger.ListAll().Count == 2 && ledger.Find(f.Raid.EventId)!.DeliveredToSnapshotId == "existing-delivery"
            && ledger.Find(f.Raid.EventId)!.AppliedSnapshotId == "settled", "Application batch lost delivery or idempotency.");
    }

    private static bool Same(AuthoritativeEvent? left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Fails(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or SqliteException) { return; }
        throw new Exception(message);
    }
    private static void WithFixture(Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-settlement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(new Fixture(root)); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class Fixture
    {
        public string Root { get; }
        public string Database => Path.Combine(Root, "server.sqlite");
        public string Journal => Path.Combine(Root, "intents");
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddHours(1);
        public SaveSnapshotPackage Original, Evidence, Edited;
        public AuthoritativeEvent Raid;
        public RaidSettlementSnapshotMutation Mutation;
        public RaidSettlementDeferredPayload Payload;
        public ClashOfRimNetworkState State = null!;
        public FileColonySnapshotIndexStore Files = null!;
        public CrashStore Store = null!;
        public Fixture(string root)
        {
            Root = root;
            ServerDatabaseMigrator.Migrate(Database);
            var data = new RaidSettlementTestData(root);
            Original = data.Original;
            Raid = data.Raid;
            Evidence = data.Evidence;
            var result = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(Raid.EventId, Original.Envelope.Identity,
                Original, Evidence, "0", LossRatio: 0.35f, ReturnedMapUniqueId: "7"));
            Require(result.Accepted, "Fixture settlement invalid: " + result.Kind);
            Edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(Original, result, "settled", Now);
            Mutation = new RaidSettlementSnapshotMutation(Raid,
                RaidSettlementLedgerRecorder.CreateSettlementEvent(Raid, result, Now, false), Original.Envelope, "settled", Now);
            Payload = data.Payload;
            Open();
            State.Ledger.Append(Raid);
            Files.StoreLatest(Original, Original.Index, Now);
            State.Players.RecordLatestSnapshotReference("defender", "defender-colony", "before", Now);
        }
        public void Open()
        {
            Files = new FileColonySnapshotIndexStore(Path.Combine(Root, "snapshots"));
            Store = new CrashStore(Files);
            State = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal));
        }
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + Database);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
    private sealed class CrashStore(FileColonySnapshotIndexStore inner) : IPreparedColonySnapshotPackageStore
    {
        public string? Fault;
        public int Writes, Preparations, PreparedWrites;
        public Action? BeforePrepare;
        public PreparedSnapshotPayload PreparePayload(byte[] bytes)
        {
            Interlocked.Increment(ref Preparations);
            BeforePrepare?.Invoke();
            return inner.PreparePayload(bytes);
        }
        public void StorePrepared(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at, PreparedSnapshotPayload prepared)
        {
            Writes++; PreparedWrites++;
            if (Fault == "before") throw new IOException("before file");
            inner.StorePrepared(package, index, at, prepared);
            if (Fault == "after") throw new IOException("after file");
        }
        public Action? AfterRead;
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            Writes++;
            if (Fault == "before") throw new IOException("before file");
            inner.StoreLatest(package, index, at);
            if (Fault == "after") throw new IOException("after file");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony)
        {
            var package = inner.GetLatestPackage(owner, colony);
            var callback = AfterRead;
            AfterRead = null;
            callback?.Invoke();
            return package;
        }
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
