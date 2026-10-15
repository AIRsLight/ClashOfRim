using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class BankEndpointRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in new[] { "borrow", "loan", "debt" })
        {
            foreach (bool final in new[] { false, true })
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "newer", "compete", "failure", "revoke-failure" })
                WithFixture(factory, kind, f => VerifyPreparing(f, final, change));
            WithFixture(factory, kind, VerifyInvalidBody);
            WithFixture(factory, kind, VerifyConcurrentRetries);
        }
    }

    private static void VerifyPreparing(Fixture f, bool final, string change)
    {
        if (!final) { f.SeedHttp(); f.Open(); }
        f.Store.Reset();
        Reply? reply = null; Exception? failure = null;
        string? baseline = null, expectedToken = null;
        int observed = 0;
        void Change()
        {
            observed++;
            expectedToken = f.ReadIntent().Package.Envelope.NextLineageToken;
            Require(!Monitor.IsEntered(f.State.BankLoans.SnapshotCommitGate), "Recovery preparation owns the bank gate.");
            if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
            if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
            if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("userA");
            if (change == "newer")
            {
                var newer = f.Package with { Envelope = f.Package.Envelope with { Identity = new("userA", "colonyA", "newer"), NextLineageToken = "newer-token" } };
                f.Store.Inner.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
            }
            if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
            if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
            baseline = f.Evidence();
        }
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook is null ? null : () =>
            {
                Require(!Monitor.IsEntered(f.State.BankLoans.SnapshotCommitGate), "Recovery preparation owns the bank gate.");
                hook();
            },
            () => { try { reply = f.Call(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                Require(f.Store.Writes == 0, "Endpoint published before preparation finished.");
                if (!final) Change();
                else
                {
                    // Introduce a durable intent while this request prepares its own upload.
                    f.SeedDirect(); f.Store.Reset();
                    f.Store.BeforePrepare = () =>
                    {
                        f.Store.BeforePrepare = null;
                        Require(!Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate)
                            && !Monitor.IsEntered(f.State.BankLoans.SnapshotCommitGate), "Final recovery preparation holds a business gate.");
                        Task.Run(() => { using (f.State.EnterSnapshotMutation()) Change(); }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    };
                }
            });
        Require(observed == 1, "Endpoint did not prepare pending recovery at its commit boundary.");
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "newer" or "failure";
        Require((failure is not null) == failed, "Unexpected bank recovery exception: " + f.Kind + "/" + final + "/" + change);
        if (denied)
            Require(reply is { Result.Accepted: false, Id: null, Snapshot: null, Token: null, Delta: 0 }
                && f.State.SnapshotCommits.HasPending && f.Store.Writes == 0 && baseline == f.Evidence(), "Revoked recovery changed or disclosed bank state.");
        else if (failed)
            Require(f.State.SnapshotCommits.HasPending && f.Store.Writes == 0 && baseline == f.Evidence(), "Failed recovery changed its intent or bank state.");
        else
            Require(reply!.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && reply.Snapshot == "paid"
                && reply.Token == expectedToken && !string.IsNullOrEmpty(expectedToken) && reply.Delta == (f.Kind == "borrow" ? 100 : -110)
                && !f.State.SnapshotCommits.HasPending && f.Store.Writes == 1
                && f.Store.PreparedWrites == (change == "compete" ? 0 : 1), "Bank recovery did not return its original confirmed result.");
        AssertGatesReleased(f);
    }

    private static void VerifyInvalidBody(Fixture f)
    {
        f.SeedHttp(); f.Open(); f.Store.Reset();
        string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Invalid body token started recovery preparation.");
        var reply = f.Call(bodyToken: "invalid");
        Require(!reply.Result.Accepted && reply.Id is null && reply.Token is null && before == f.Evidence()
            && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Invalid body token recovered pending bank work.");
        AssertGatesReleased(f);
    }

    private static void VerifyConcurrentRetries(Fixture f)
    {
        f.SeedHttp(); f.Open(); f.Store.Reset();
        string expectedToken = f.ReadIntent().Package.Envelope.NextLineageToken!;
        var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Call()), () => { });
        Require(replies.All(r => r.Result.Accepted && r.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && r.Token == expectedToken)
            && replies.Select(r => r.Token).Distinct().Count() == 1 && replies.Select(r => r.Id).Distinct().Count() == 1
            && f.Store.Writes == 1 && f.Store.PreparedWrites == 1 && !f.State.SnapshotCommits.HasPending,
            "Concurrent recovered bank requests did not converge on one publication.");
        AssertGatesReleased(f);
    }

    private static void AssertGatesReleased(Fixture f)
        => Task.Run(() =>
        {
            if (!Monitor.TryEnter(f.State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3))) throw new Exception("Recovery leaked the mutation gate.");
            try
            {
                if (!Monitor.TryEnter(f.State.BankLoans.SnapshotCommitGate, TimeSpan.FromSeconds(3))) throw new Exception("Recovery leaked the bank gate.");
                Monitor.Exit(f.State.BankLoans.SnapshotCommitGate);
            }
            finally { Monitor.Exit(f.State.RaidSettlementSnapshotMutationGate); }
        }).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();

    private static void WithFixture(Func<string, SaveSnapshotPackage> factory, string kind, Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-bank-recovery-" + Guid.NewGuid().ToString("N"));
        try { run(new(root, factory, kind)); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed record Reply(ProtocolResponse Result, string? Id, string? Snapshot, string? Token, int Delta);

    private sealed class Fixture
    {
        private readonly string root, database;
        private string token = "", debtId = "";
        public string Kind { get; }
        public string Journal => Path.Combine(root, "journal");
        public SaveSnapshotPackage Package { get; }
        public ClashOfRimNetworkState State = null!;
        public Store Store = null!;
        public Fixture(string root, Func<string, SaveSnapshotPackage> factory, string kind)
        {
            this.root = root; Kind = kind;
            database = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(database); Open();
            Store.Gate = null; // Seed the baseline outside the endpoint under test.
            Package = MercenaryCommitCrashTests.PrepareEndpoint(State, factory, new("userA", "colonyA", "before"), "paid");
            if (kind == "loan")
            {
                State.BankLoans.Create("seed", "userA", "colonyA", "before", 100, 10, 1, 1000, DateTimeOffset.UtcNow, "loan");
                State.BankLoans.ConfirmPendingForSnapshot("userA", "colonyA", "before", 1000, DateTimeOffset.UtcNow);
            }
            if (kind == "debt") debtId = State.BankLoans.CreateDebt("seed", "userA", "colonyA", "before", 110, "Test", "test", "source", 1000, DateTimeOffset.UtcNow).DebtId;
            Store.Gate = State.RaidSettlementSnapshotMutationGate;
        }
        public void Open()
        {
            Store = new(Path.Combine(root, "snapshots"));
            State = new(snapshotStore: Store, snapshotCommits: new SnapshotCommitService(Journal),
                bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null),
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(database), null), ledger: new SqliteAuthoritativeEventLedger(database),
                serverConfiguration: new(bankLoansEnabled: true, bankMinLoanSilver: 1, bankMinDurationDays: 1, bankBaseAnnualInterestRate: 0));
            Store.Gate = State.RaidSettlementSnapshotMutationGate;
            State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out string session);
            token = State.AuthTokens.Issue("userA", "userA", "colonyA", session, DateTimeOffset.UtcNow);
        }
        public Reply Call(string? bodyToken = null)
        {
            var metadata = ProtocolDtoMapper.ToMetadataDto(Package);
            object request = Kind == "borrow" ? new CreateBankLoanWithSnapshotRequest("operation", "userA", "colonyA", "before", bodyToken ?? token, 1100, 10000, 100, 1, "loan-operation", 0, metadata)
                : Kind == "loan" ? new RepayBankLoanWithSnapshotRequest("operation", "userA", "colonyA", "before", bodyToken ?? token, 1100, "loan", 110, metadata)
                : new RepayBankDebtWithSnapshotRequest("operation", "userA", "colonyA", "before", bodyToken ?? token, 1100, debtId, 110, metadata);
            if (Kind == "debt")
            {
                var r = SnapshotEndpointTestClient.Invoke<BankDebtResponse>("RepayBankDebtWithSnapshot", State, _ => request,
                    Package.Payload, new("userA", "colonyA", "before"), token).GetAwaiter().GetResult();
                return new(r.Result, r.Debt?.DebtId, r.AppliedSnapshotId, r.NextLineageToken, r.SilverDelta);
            }
            var response = SnapshotEndpointTestClient.Invoke<BankLoanResponse>(Kind == "borrow" ? "CreateBankLoanWithSnapshot" : "RepayBankLoanWithSnapshot",
                State, _ => request, Package.Payload, new("userA", "colonyA", "before"), token).GetAwaiter().GetResult();
            return new(response.Result, response.Loan?.LoanId, response.AppliedSnapshotId, response.NextLineageToken, response.SilverDelta);
        }
        public void SeedHttp()
        {
            Store.FailPublication = true;
            bool failed = false;
            try { Call(); } catch (IOException) { failed = true; }
            Store.FailPublication = false;
            Require(failed && State.SnapshotCommits.HasPending, "Could not interrupt bank HTTP commit.");
        }
        public void SeedDirect()
        {
            var upload = new SnapshotUploadReceiver(Store).Receive(new("userA", "colonyA", "paid"), Package, DateTimeOffset.UtcNow, storeAccepted: false);
            Require(upload.Accepted, "Could not seed competing journal.");
            var snapshot = upload.AcceptedSnapshot!;
            BankLoanRecord? loan = Kind == "borrow" ? State.BankLoans.PrepareActivatedLoan("operation", "userA", "colonyA", "paid", 100, 0, 1, 1100, DateTimeOffset.UtcNow, "loan-operation")
                : Kind == "loan" ? State.BankLoans.GetActive("userA", "colonyA")! with { Status = BankLoanRegistry.StatusRepaid, SnapshotId = "paid", RepaymentIdempotencyKey = "operation" } : null;
            BankDebtRecord? debt = Kind == "debt" ? State.BankLoans.FindDebt("userA", "colonyA", debtId)! with
                { Status = BankLoanRegistry.StatusRepaid, SnapshotId = "paid", RepaymentIdempotencyKey = "operation" } : null;
            Store.FailPublication = true;
            try { State.SnapshotCommits.Commit(Store, State.BankLoans, Package, snapshot, loan, debt, (_, _) => { }); }
            catch (IOException) { }
            finally { Store.FailPublication = false; }
            Require(State.SnapshotCommits.HasPending, "Could not seed competing intent.");
        }
        public SnapshotCommitIntent ReadIntent() => JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllText(Directory.GetFiles(Journal, "*.json").Single()))!;
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest().Select(r => new { r.Envelope, r.AcceptedAtUtc }),
            loan = State.BankLoans.FindByIdempotencyKey("operation"), debt = State.BankLoans.FindDebtByIdempotencyKey("operation"),
            journal = Directory.EnumerateFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray() });
    }
    private sealed class Store(string root) : IPreparedColonySnapshotPackageStore
    {
        public FileColonySnapshotIndexStore Inner { get; } = new(root);
        public Action? BeforePrepare;
        public object? Gate;
        public bool FailPublication, FailPreparation;
        public int PreparedWrites, RawWrites;
        public int Writes => PreparedWrites + RawWrites;
        public void Reset() { PreparedWrites = 0; RawWrites = 0; }
        public PreparedSnapshotPayload PreparePayload(byte[] bytes)
        {
            BeforePrepare?.Invoke();
            if (FailPreparation) throw new IOException("preparation failed");
            return Inner.PreparePayload(bytes);
        }
        public void StorePrepared(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at, PreparedSnapshotPayload prepared)
        {
            Require(Gate is null || Monitor.IsEntered(Gate), "Bank recovery scope did not retain the mutation gate for publication.");
            if (FailPublication) throw new IOException("publication failed");
            Inner.StorePrepared(p, i, at, prepared); PreparedWrites++;
        }
        public void StoreLatest(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at)
        {
            Require(Gate is null || Monitor.IsEntered(Gate), "Bank recovery scope did not retain the mutation gate for publication.");
            if (FailPublication) throw new IOException("publication failed");
            Inner.StoreLatest(p, i, at); RawWrites++;
        }
        public void StoreLatest(LatestSnapshotRecord record) => Inner.StoreLatest(record);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => Inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => Inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => Inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => Inner.RemoveLatest(owner, colony);
    }
}
