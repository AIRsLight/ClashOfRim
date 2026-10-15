using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class SnapshotEndpointRecoveryPreparationTests
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

    public static void RunServices(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in new[] { "shop-sell", "shop-buy", "mercenary", "guard" })
        {
            foreach (bool final in new[] { false, true })
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "newer", "compete", "failure", "revoke-failure" })
                WithFixture(factory, kind, f => VerifyPreparing(f, final, change));
            foreach (string change in new[] { "price", "capacity" })
                WithFixture(factory, kind, f => VerifyFinalTerms(f, change));
            if (kind == "shop-buy") WithFixture(factory, kind, f => VerifyFinalTerms(f, "item"));
            if (!kind.StartsWith("shop")) WithFixture(factory, kind, VerifyInvalidBody);
            WithFixture(factory, kind, VerifyConcurrentRetries);
        }
    }

    private static void VerifyFinalTerms(Fixture f, string change)
    {
        f.Store.Reset();
        Reply? reply = null;
        string? before = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { f.ChangeTerms(change); before = f.Evidence(); });
        Require(reply is { Result.Accepted: false, Snapshot: null, Token: null } && f.Store.Writes == 0
            && !f.State.SnapshotCommits.HasPending && before == f.Evidence(),
            "Final validation accepted changed business terms: " + f.Kind + "/" + change);
        AssertGatesReleased(f);
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
            Require(!Monitor.IsEntered(f.DomainGate), "Recovery preparation owns the domain gate.");
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
                Require(!Monitor.IsEntered(f.DomainGate), "Recovery preparation owns the domain gate.");
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
                            && !Monitor.IsEntered(f.DomainGate), "Final recovery preparation holds a business gate.");
                        Task.Run(() => { using (f.State.EnterSnapshotMutation()) Change(); }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    };
                }
            });
        Require(observed == 1, "Endpoint did not prepare pending recovery at its commit boundary.");
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "newer" or "failure";
        Require((failure is not null) == failed, "Unexpected snapshot recovery exception: " + f.Kind + "/" + final + "/" + change);
        if (denied)
            Require(reply is { Result.Accepted: false, Snapshot: null, Token: null, Delta: 0, HasBankStatus: false }
                && reply.Id == f.DeniedId && f.State.SnapshotCommits.HasPending && f.Store.Writes == 0 && baseline == f.Evidence(), "Revoked recovery changed or disclosed business state.");
        else if (failed)
            Require(f.State.SnapshotCommits.HasPending && f.Store.Writes == 0 && baseline == f.Evidence(), "Failed recovery changed its intent or business state.");
        else
            Require(reply!.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && reply.Snapshot == "paid"
                && reply.Token == expectedToken && !string.IsNullOrEmpty(expectedToken) && reply.Delta == f.ExpectedDelta && reply.Id == f.ExpectedId && f.BusinessCommitted
                && !f.State.SnapshotCommits.HasPending && f.Store.Writes == 1
                && f.Store.PreparedWrites == (change == "compete" ? 0 : 1), "Snapshot recovery did not return its original confirmed result.");
        AssertGatesReleased(f);
    }

    private static void VerifyInvalidBody(Fixture f)
    {
        f.SeedHttp(); f.Open(); f.Store.Reset();
        string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Invalid body token started recovery preparation.");
        var reply = f.Call(bodyToken: "invalid");
        Require(!reply.Result.Accepted && reply.Id is null && reply.Token is null && !reply.HasBankStatus && before == f.Evidence()
            && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Invalid body token recovered pending business work.");
        AssertGatesReleased(f);
    }

    private static void VerifyConcurrentRetries(Fixture f)
    {
        f.SeedHttp(); f.Open(); f.Store.Reset();
        string expectedToken = f.ReadIntent().Package.Envelope.NextLineageToken!;
        var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Call()), () => { });
        Require(replies.All(r => r.Result.Accepted && r.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && r.Token == expectedToken)
            && replies.Select(r => r.Token).Distinct().Count() == 1 && replies.Select(r => r.Id).Distinct().Count() == 1
            && f.Store.Writes == 1 && f.Store.PreparedWrites == 1 && !f.State.SnapshotCommits.HasPending && f.BusinessCommitted,
            "Concurrent recovered snapshot requests did not converge on one publication.");
        AssertGatesReleased(f);
    }

    private static void AssertGatesReleased(Fixture f)
        => Task.Run(() =>
        {
            if (!Monitor.TryEnter(f.State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3))) throw new Exception("Recovery leaked the mutation gate.");
            try
            {
                if (!Monitor.TryEnter(f.DomainGate, TimeSpan.FromSeconds(3))) throw new Exception("Recovery leaked the domain gate.");
                Monitor.Exit(f.DomainGate);
            }
            finally { Monitor.Exit(f.State.RaidSettlementSnapshotMutationGate); }
        }).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();

    private static void WithFixture(Func<string, SaveSnapshotPackage> factory, string kind, Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-endpoint-recovery-" + Guid.NewGuid().ToString("N"));
        try { run(new(root, factory, kind)); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed record Reply(ProtocolResponse Result, string? Id, string? Snapshot, string? Token, int Delta, bool HasBankStatus = false);

    private sealed class Fixture
    {
        private readonly string root, database;
        private string token = "", debtId = "";
        public string Kind { get; }
        private bool Shop => Kind.StartsWith("shop");
        private bool Hire => Kind is "mercenary" or "guard";
        private string ShopKind => Kind == "shop-buy" ? ServerShopListingKinds.BuyFromPlayer : ServerShopListingKinds.SellToPlayer;
        private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");
        public object DomainGate => Shop ? State.ServerShop.SnapshotCommitGate : Kind == "mercenary"
            ? State.MercenaryContracts.SnapshotCommitGate : Kind == "guard" ? State.MercenaryGuards.SnapshotCommitGate : State.BankLoans.SnapshotCommitGate;
        public string? DeniedId => Shop ? "listing" : null;
        public string ExpectedId => Shop ? "listing" : Hire ? "contract" : Kind == "borrow" ? "loan-operation" : Kind == "loan" ? "loan" : debtId;
        public int ExpectedDelta => Shop ? 2 : Hire ? 0 : Kind == "borrow" ? 100 : -110;
        public bool BusinessCommitted => Shop ? State.ServerShop.Find("listing")!.StockCount == 2
            && State.ServerShop.GetBuyerPurchaseCount("listing", Actor) == 1 && State.ServerShop.FindReceipt("operation")?.SnapshotId == "paid"
            : Kind == "mercenary" ? State.MercenaryContracts.FindByIdempotencyKey("operation") is { ContractId: "contract", SnapshotId: "paid", Status: MercenaryContractRegistry.StatusActive }
                && State.MercenaryContracts.CountOpenForColony("userA", "colonyA") == 1
            : Kind != "guard" || State.MercenaryGuards.FindByIdempotencyKey("operation") is { ContractId: "contract", SnapshotId: "paid", Status: MercenaryGuardContractRegistry.StatusActive };
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
            if (Shop) ShopCommitCrashTests.Seed(State.ServerShop, kind: ShopKind);
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
                serverShop: new ServerShopRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.ServerShop), null),
                mercenaryContracts: new MercenaryContractRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryContracts), null),
                mercenaryGuards: new MercenaryGuardContractRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryGuards), null),
                serverConfiguration: new(pvpEnabled: true, maxActiveMercenariesPerColony: 1, bankLoansEnabled: true, bankMinLoanSilver: 1, bankMinDurationDays: 1, bankBaseAnnualInterestRate: 0));
            Store.Gate = State.RaidSettlementSnapshotMutationGate;
            State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out string session);
            token = State.AuthTokens.Issue("userA", "userA", "colonyA", session, DateTimeOffset.UtcNow);
        }
        public void ChangeTerms(string change)
        {
            if (Shop) State.ServerShop.Upsert("listing", ShopKind, new("steel", change == "item" ? "WoodLog" : "Steel", 1),
                change == "price" ? 11 : 10, change == "capacity" ? 0 : 3, 1,
                ServerShopQualityRequirementModes.AtLeast, ServerShopHitPointsRequirementModes.AtLeast, "admin", DateTimeOffset.UtcNow);
            else if (change == "price") State.UpdateServerConfiguration(new(pvpEnabled: true,
                mercenaryHarmfulSurgeryFineSilver: 1001, mercenaryGuardApprenticeSilver: 601));
            else if (Kind == "guard") State.MercenaryGuards.ActivateWithSnapshot("other", "userA", "colonyA", "other", "before",
                "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UtcNow);
            else State.MercenaryContracts.ActivateWithSnapshot("other", "userA", "colonyA", "other", "before",
                "Shooting", 7, 7, 560, 1000, 1500, 1000, DateTimeOffset.UtcNow);
        }
        public Reply Call(string? bodyToken = null)
        {
            var metadata = ProtocolDtoMapper.ToMetadataDto(Package);
            if (Shop)
            {
                var r = SnapshotEndpointTestClient.Invoke<PurchaseServerShopListingResponse>("PurchaseServerShopListingWithSnapshot", State,
                    _ => new PurchaseServerShopListingWithSnapshotRequest(ShopCommitCrashTests.Request("operation", ShopKind, Actor), metadata),
                    Package.Payload, Actor, token).GetAwaiter().GetResult();
                return new(r.Result, r.ListingId, r.AppliedSnapshotId, r.NextLineageToken, r.RemainingStockCount);
            }
            if (Kind == "mercenary")
            {
                var r = SnapshotEndpointTestClient.Invoke<MercenaryHireResponse>("HireMercenaryWithSnapshot", State,
                    _ => new HireMercenaryWithSnapshotRequest("operation", "userA", "colonyA", "before", bodyToken ?? token,
                        1000, "Shooting", 7, 7, "contract", 560, 1000, 1500, metadata), Package.Payload, Actor, token).GetAwaiter().GetResult();
                return new(r.Result, r.Contract?.ContractId, r.AppliedSnapshotId, r.NextLineageToken, 0, r.BankStatus is not null);
            }
            if (Kind == "guard")
            {
                var r = SnapshotEndpointTestClient.Invoke<MercenaryGuardHireResponse>("HireMercenaryGuardWithSnapshot", State,
                    _ => new HireMercenaryGuardWithSnapshotRequest("operation", "userA", "colonyA", "before", bodyToken ?? token,
                        1000, "contract", "Apprentice", 600, 0.35f, metadata), Package.Payload, Actor, token).GetAwaiter().GetResult();
                return new(r.Result, r.Contract?.ContractId, r.AppliedSnapshotId, r.NextLineageToken, 0, r.BankStatus is not null);
            }
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
            Require(failed && State.SnapshotCommits.HasPending, "Could not interrupt snapshot HTTP commit.");
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
            try
            {
                if (Shop) State.SnapshotCommits.CommitShop(Store, State.BankLoans, State.ServerShop, Package, snapshot,
                    State.ServerShop.PrepareSnapshotCommit(ShopCommitCrashTests.Request("operation", ShopKind, Actor), snapshot), (_, _) => { });
                else if (Hire)
                {
                    var at = DateTimeOffset.UtcNow;
                    var mutation = Kind == "guard" ? new MercenarySnapshotMutation(Guard: new("contract", "operation", "userA", "colonyA", "paid",
                        "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UtcNow, MercenaryGuardContractRegistry.StatusActive, null, null))
                        : new MercenarySnapshotMutation(Contract: new("contract", "operation", "userA", "colonyA", "paid",
                            "Shooting", 7, 7, 560, 1000, 1500, 1000, 1000 + 7 * BankLoanPolicy.GameTicksPerDay,
                            at, MercenaryContractRegistry.StatusActive, at), MaximumOpenContracts: 1);
                    State.SnapshotCommits.CommitMercenary(Store, State.BankLoans, State.MercenaryContracts, State.MercenaryGuards,
                        Package, snapshot, mutation, (_, _) => { });
                }
                else State.SnapshotCommits.Commit(Store, State.BankLoans, Package, snapshot, loan, debt, (_, _) => { });
            }
            catch (IOException) { }
            finally { Store.FailPublication = false; }
            Require(State.SnapshotCommits.HasPending, "Could not seed competing intent.");
        }
        public SnapshotCommitIntent ReadIntent() => JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllText(Directory.GetFiles(Journal, "*.json").Single()))!;
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest().Select(r => new { r.Envelope, r.AcceptedAtUtc }),
            loan = State.BankLoans.FindByIdempotencyKey("operation"), debt = State.BankLoans.FindDebtByIdempotencyKey("operation"),
            listing = State.ServerShop.Find("listing"), receipt = State.ServerShop.FindReceipt("operation"),
            contract = State.MercenaryContracts.FindByIdempotencyKey("operation"), guard = State.MercenaryGuards.FindByIdempotencyKey("operation"),
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
            Require(Gate is null || Monitor.IsEntered(Gate), "Snapshot recovery scope did not retain the mutation gate for publication.");
            if (FailPublication) throw new IOException("publication failed");
            Inner.StorePrepared(p, i, at, prepared); PreparedWrites++;
        }
        public void StoreLatest(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at)
        {
            Require(Gate is null || Monitor.IsEntered(Gate), "Snapshot recovery scope did not retain the mutation gate for publication.");
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
