using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Reflection;

internal static class SnapshotJournalRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        RunLifecycleBoundaries(factory);
        foreach (bool reopen in new[] { false, true })
        foreach (bool after in new[] { false, true })
        foreach (string change in new[] { "none", "newer", "token", "remove", "published", "compete", "replace", "failure", "obsolete-failure" })
            WithFixture(factory, f => VerifyPaused(f, reopen, after, change));
        foreach (string mode in new[] { "published", "prepared", "nested", "legacy" })
            WithFixture(factory, f => VerifyExistingBoundaries(f, mode));
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "before", "after", "bank", "processor" })
            WithFixture(factory, f => VerifyRecoveryFailure(f, reopen, fault));
        WithFixture(factory, VerifyRollbackInBatch);
    }

    public static void RunLifecycleBoundaries(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string entry in new[] { "pending", "login", "abandoned" })
        foreach (string mode in new[] { "prepare", "reopen", "nested", "failure" })
            WithFixture(factory, f =>
            {
                f.Seed("evidence");
                if (mode == "reopen") f.Open();
                f.Store.Reset();
                if (mode == "nested")
                {
                    using (f.State.EnterSnapshotMutation())
                    {
                        CallEntry(f, entry);
                        Require(Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate), "Nested cleanup released its caller's gate.");
                    }
                }
                else if (mode == "failure")
                {
                    f.Store.FailPreparation = true;
                    bool failed = false;
                    try { CallEntry(f, entry); } catch (IOException) { failed = true; }
                    Require(failed && f.State.SnapshotCommits.HasPending && f.Store.Writes == 0, "Cleanup swallowed recovery failure.");
                    f.Store.FailPreparation = false;
                    CallEntry(f, entry);
                }
                else SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
                    hook => f.Store.BeforePrepare = hook, () => CallEntry(f, entry),
                    () => Require(f.Store.Writes == 0 && f.PostCalls == 0, "Lifecycle published during preparation."));
                Require(!f.State.SnapshotCommits.HasPending && f.PostCalls == 1 && f.Store.Writes == 1
                    && f.Store.RawWrites == (mode == "nested" ? 1 : 0)
                    && f.State.BankLoans.FindByIdempotencyKey("commit-evidence") is not null, "Cleanup did not recover once: " + entry + "/" + mode);
                Require(!Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate), "Cleanup leaked its mutation gate.");
            });
        foreach (string kind in new[] { "LoggedIn", "InitialWorldSessionPrepared", "ColonyAbandoned" })
        foreach (string invalid in new[] { "none", "user", "colony" })
            WithFixture(factory, f =>
            {
                f.Seed("evidence"); f.Store.Reset();
                string before = f.PersistentState();
                // The other lifecycle handler owns this event; empty identity belongs to neither.
                string hook = kind == "ColonyAbandoned" ? "ReconcilePendingConfirmationsOnClientLifecycle" : "ReconcileAbandonedColonyOnClientLifecycle";
                Invoke(hook, f.State, Lifecycle(kind, invalid == "user" ? "" : "userA", invalid == "colony" ? "" : "colonyA"));
                if (invalid != "none")
                    Invoke(kind == "ColonyAbandoned" ? "ReconcileAbandonedColonyOnClientLifecycle" : "ReconcilePendingConfirmationsOnClientLifecycle",
                        f.State, Lifecycle(kind, invalid == "user" ? "" : "userA", invalid == "colony" ? "" : "colonyA"));
                Require(f.State.SnapshotCommits.HasPending && f.Store.Preparations == 0 && f.Store.Writes == 0
                    && f.PostCalls == 0 && before == f.PersistentState(), "Unrelated lifecycle hook started recovery.");
            });
    }

    private static void CallEntry(Fixture f, string entry)
    {
        if (entry == "pending") Invoke("ReconcilePendingConfirmationsForColony", f.State, "userA", "colonyA", DateTimeOffset.UtcNow, true);
        else Invoke(entry == "login" ? "ReconcilePendingConfirmationsOnClientLifecycle" : "ReconcileAbandonedColonyOnClientLifecycle",
            f.State, Lifecycle(entry == "login" ? "LoggedIn" : "ColonyAbandoned", "userA", "colonyA"));
    }

    private static object Lifecycle(string kind, string user, string colony)
    {
        return new AIRsLight.ClashOfRim.Network.Modules.Sessions.ClientLifecycleEvent(
            Enum.Parse<AIRsLight.ClashOfRim.Network.Modules.Sessions.ClientLifecycleEventKind>(kind), user, colony, "evidence", "session", DateTimeOffset.UtcNow);
    }

    private static void Invoke(string name, params object[] args)
    {
        try { typeof(ClashOfRimNetworkServer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
    }

    private static void VerifyPaused(Fixture f, bool reopen, bool after, string change)
    {
        f.Seed("evidence");
        if (reopen) f.Open();
        f.Store.Reset();
        Exception? failure = null;
        string? baseline = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
            hook => { if (after) f.Store.AfterPrepare = hook; else f.Store.BeforePrepare = hook; },
            () => { try { f.Recover(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                Require(f.Store.Writes == 0 && f.State.BankLoans.FindByIdempotencyKey("commit-evidence") is null && f.PostCalls == 0,
                    "Journal recovery published before preparation was validated.");
                if (change == "newer") f.Publish(f.Evidence with { Envelope = f.Evidence.Envelope with { Identity = new("userA", "colonyA", "newer") } });
                if (change == "token") f.Publish(f.Before with { Envelope = f.Before.Envelope with { NextLineageToken = "changed" } });
                if (change == "remove") f.Store.Inner.RemoveLatest("userA", "colonyA");
                if (change == "published") f.Publish(f.Evidence);
                if (change is "compete" or "replace" or "obsolete-failure")
                {
                    // This caller already owns the business gate, exercising the retained synchronous path.
                    f.Recover();
                    Require(!f.State.SnapshotCommits.HasPending, "Competing recovery did not finish.");
                    if (change == "replace") f.Seed("replacement");
                    f.Store.ResetWrites();
                }
                if (change is "failure" or "obsolete-failure") f.Store.FailPreparation = true;
                baseline = f.PersistentState();
            });
        bool rejected = change is "newer" or "token" or "remove" or "failure";
        Require((failure is not null) == rejected, "Recovery preparation outcome mismatch: " + reopen + "/" + after + "/" + change);
        if (rejected)
            Require(f.State.SnapshotCommits.HasPending && f.Store.Writes == 0 && f.PostCalls == 0 && baseline == f.PersistentState(),
                "Rejected recovery changed its durable intent, bank or destination.");
        else
        {
            string expected = change == "replace" ? "replacement" : "evidence";
            Require(!f.State.SnapshotCommits.HasPending && f.State.BankLoans.FindByIdempotencyKey("commit-" + expected) is not null
                && f.Store.Inner.GetLatest("userA", "colonyA")!.Identity.SnapshotId == expected, "Recovery lost its current intent.");
            Require(f.PostCalls == (change == "replace" ? 2 : 1), "Obsolete recovery replayed postprocessors.");
            int writes = change is "none" or "replace" ? 1 : 0;
            Require(f.Store.Writes == writes && f.Store.PreparedWrites == writes && f.Store.RawWrites == 0,
                "Recovery fell back to unprepared publication or republished an obsolete intent.");
            Require(f.Store.Preparations == (change == "replace" ? 2 : 1), "Recovery did not reuse or recapture its prepared payload.");
        }
    }

    private static void VerifyExistingBoundaries(Fixture f, string mode)
    {
        f.Seed("evidence", fault: mode == "published" ? "after" : "before", prepared: mode == "prepared");
        if (mode != "prepared") f.Open(legacy: mode == "legacy");
        f.Store.Reset();
        f.Store.BeforePrepare = () => throw new Exception("Unexpected recovery compression.");
        if (mode == "nested") { using (f.State.EnterSnapshotMutation()) f.Recover(); }
        else f.Recover();
        Require(!f.State.SnapshotCommits.HasPending && f.State.BankLoans.FindByIdempotencyKey("commit-evidence") is not null,
            "Existing recovery boundary failed: " + mode);
        Require(f.Store.RawWrites == (mode is "nested" or "legacy" ? 1 : 0) && f.Store.PreparedWrites == (mode == "prepared" ? 1 : 0),
            "Already published/prepared or custom store recovery changed behavior: " + mode);
    }

    private static void VerifyRecoveryFailure(Fixture f, bool reopen, string fault)
    {
        f.Seed("evidence"); f.Open(); f.Store.Reset();
        f.Store.Fault = fault;
        f.FailProcessor = fault == "processor";
        if (fault == "bank") f.Sql("create trigger fail_bank before insert on server_bank_loans begin select raise(abort, 'bank failure'); end;");
        bool failed = false;
        try { f.Recover(); } catch (Exception ex) when (ex is IOException or SqliteException) { failed = true; }
        Require(failed && f.State.SnapshotCommits.HasPending && Directory.EnumerateFiles(f.Journal, "*.json").Count() == 1,
            "Recovery failure lost the durable intent: " + fault);
        int initialWrites = f.Store.PreparedWrites;
        Require(initialWrites == (fault == "before" ? 0 : 1) && f.Store.RawWrites == 0, "Recovery fault crossed the wrong publication boundary.");
        if (fault == "bank") f.Sql("drop trigger fail_bank;");
        f.Store.Fault = null; f.FailProcessor = false;
        if (reopen) f.Open();
        f.Store.Reset(); f.Recover();
        Require(!f.State.SnapshotCommits.HasPending && f.Store.PreparedWrites == (fault == "before" ? 1 : 0)
            && f.Store.RawWrites == 0 && f.Store.Preparations == (reopen && fault == "before" ? 1 : 0), "Failed recovery did not safely resume.");
        string before = f.PersistentState(); f.Recover(); f.Open(); f.Recover();
        Require(before == f.PersistentState() && f.State.BankLoans.FindByIdempotencyKey("commit-evidence") is not null, "Repeated recovery changed its durable result.");
    }

    private static void VerifyRollbackInBatch(Fixture f)
    {
        f.Seed("evidence", fault: "after");
        var first = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllText(Directory.GetFiles(f.Journal, "*.json").Single()))!;
        var second = first with
        {
            Id = Guid.NewGuid().ToString("N"), PreviousSnapshotId = null, PreviousLineageToken = null,
            Package = first.Package with { Envelope = first.Package.Envelope with { Identity = new("userB", "colonyB", "other") } },
            Loan = first.Loan! with { LoanId = "other-loan", IdempotencyKey = "other", UserId = "userB", ColonyId = "colonyB", SnapshotId = "other" }
        };
        File.WriteAllText(Path.Combine(f.Journal, second.Id + ".json"), JsonSerializer.Serialize(second));
        f.Open();
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            f.Recover, () => f.Publish(f.Before));
        Require(!f.State.SnapshotCommits.HasPending && f.Store.RawWrites == 0 && f.Store.PreparedWrites == 2 && f.Store.Preparations == 3,
            "Batch recovery compressed a rolled-back destination under the mutation gate.");
        Require(f.State.BankLoans.FindByIdempotencyKey("commit-evidence") is not null && f.State.BankLoans.FindByIdempotencyKey("other") is not null,
            "Batch recovery lost a journal entry.");
    }

    private static void WithFixture(Func<string, SaveSnapshotPackage> factory, Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-journal-prepare-" + Guid.NewGuid().ToString("N"));
        try { run(new Fixture(root, factory)); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Fixture
    {
        private readonly string root, database;
        private readonly Func<string, SaveSnapshotPackage> factory;
        public string Journal => Path.Combine(root, "journal");
        public SaveSnapshotPackage Before { get; }
        public SaveSnapshotPackage Evidence = null!;
        public ObservedStore Store = null!;
        public ClashOfRimNetworkState State = null!;
        public int PostCalls;
        public bool FailProcessor;
        public Fixture(string root, Func<string, SaveSnapshotPackage> factory)
        {
            this.root = root; this.factory = factory;
            database = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(database);
            Before = factory("before"); Before = Before with { Envelope = Before.Envelope with { NextLineageToken = "before-token" } };
            Open(); Publish(Before);
        }
        public void Open(bool legacy = false)
        {
            Store = new ObservedStore(Path.Combine(root, "snapshots"));
            State = new(snapshotStore: legacy ? new LegacyStore(Store) : Store, snapshotCommits: new SnapshotCommitService(Journal),
                bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null),
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(database), null), ledger: new SqliteAuthoritativeEventLedger(database),
                plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.recovery", "recovery", "1", "test", "", [])
                { SnapshotPostUploadProcessors = [new Processor(() => { PostCalls++; if (FailProcessor) throw new IOException("processor failure"); })] }]));
        }
        public void Seed(string id, string fault = "before", bool prepared = false)
        {
            Evidence = factory(id);
            Evidence = Evidence with { Envelope = Evidence.Envelope with { NextLineageToken = id + "-token" } };
            var snapshot = new LatestSnapshotRecord(Evidence.Envelope.Identity, Evidence.Envelope, Evidence.Index, DateTimeOffset.UtcNow);
            var loan = State.BankLoans.PrepareActivatedLoan("commit-" + id, "userA", "colonyA", id, 100, 0, 30, 1000, snapshot.AcceptedAtUtc, "loan-" + id);
            Store.Fault = fault;
            bool failed = false;
            try { State.SnapshotCommits.Commit(Store, State.BankLoans, Evidence, snapshot, loan, null, (_, _) => { },
                preparedStoragePayload: prepared ? Store.Inner.PreparePayload(Evidence.Payload) : null); }
            catch (IOException) { failed = true; }
            Store.Fault = null;
            Require(failed && State.SnapshotCommits.HasPending, "Could not seed durable intent.");
        }
        public void Recover() => ClashOfRimNetworkServer.RecoverSnapshotCommits(State);
        public void Publish(SaveSnapshotPackage package) => Store.Inner.StoreLatest(package, package.Index, DateTimeOffset.UtcNow);
        public string PersistentState() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest().Select(item => new { item.Envelope, item.AcceptedAtUtc }),
            bank = State.BankLoans.FindByIdempotencyKey("commit-evidence"),
            journals = Directory.EnumerateFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray() });
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + database); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
    private sealed class Processor(Action process) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.recovery.processor";
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.Notification;
        public int Order => 0;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) => process();
    }
    private sealed class ObservedStore(string root) : IPreparedColonySnapshotPackageStore
    {
        public FileColonySnapshotIndexStore Inner { get; } = new(root);
        public Action? BeforePrepare, AfterPrepare;
        public string? Fault;
        public bool FailPreparation;
        public int Preparations, PreparedWrites, RawWrites;
        public int Writes => PreparedWrites + RawWrites;
        public void ResetWrites() { PreparedWrites = 0; RawWrites = 0; }
        public void Reset() { ResetWrites(); Preparations = 0; }
        public PreparedSnapshotPayload PreparePayload(byte[] bytes)
        {
            Preparations++; BeforePrepare?.Invoke();
            var payload = Inner.PreparePayload(bytes); AfterPrepare?.Invoke();
            if (FailPreparation) throw new IOException("preparation failed");
            return payload;
        }
        public void StorePrepared(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at, PreparedSnapshotPayload bytes)
        {
            if (Fault == "before") throw new IOException("before publication");
            Inner.StorePrepared(p, i, at, bytes); PreparedWrites++;
            if (Fault == "after") throw new IOException("after publication");
        }
        public void StoreLatest(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at)
        {
            if (Fault == "before") throw new IOException("before publication");
            Inner.StoreLatest(p, i, at); RawWrites++;
            if (Fault == "after") throw new IOException("after publication");
        }
        public void StoreLatest(LatestSnapshotRecord record) => Inner.StoreLatest(record);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => Inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => Inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => Inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => Inner.RemoveLatest(owner, colony);
    }
    private sealed class LegacyStore(ObservedStore inner) : IColonySnapshotPackageStore
    {
        public void StoreLatest(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at) => inner.StoreLatest(p, i, at);
        public void StoreLatest(LatestSnapshotRecord record) => inner.StoreLatest(record);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
