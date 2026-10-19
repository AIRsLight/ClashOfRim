using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class PendingOperationCommitCrashTests
{
    private static readonly DateTimeOffset Accepted = DateTimeOffset.UnixEpoch.AddDays(1);

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool reopen in new[] { false, true })
        foreach (bool repayment in new[] { false, true })
        foreach (int fault in Enumerable.Range(0, 6))
            VerifyRecovery(fixture, reopen, repayment, fault);
        foreach (bool legacy in new[] { false, true }) VerifyPrimaryCommit(fixture, legacy);
        VerifyReceiveAndLifecycle(fixture);
    }

    private static void VerifyRecovery(Func<string, SaveSnapshotPackage> fixture, bool reopen, bool repayment, int fault)
    {
        WithStorage((database, root) =>
        {
            var bank = Bank(database);
            var contracts = Contracts(database);
            Seed(bank, contracts, repayment);
            string snapshots = Path.Combine(root, "snapshots"), journal = Path.Combine(root, "intents");
            var files = new FileColonySnapshotIndexStore(snapshots);
            var commits = new SnapshotCommitService(journal);
            var package = fixture("confirmed");
            var snapshot = new SnapshotUploadReceiver(files).Receive(new SnapshotUploadContext("userA", "colonyA", "confirmed"),
                package, Accepted, storeAccepted: false).AcceptedSnapshot!;
            var batch = PendingSnapshotOperations.Capture(bank, contracts, snapshot);
            string? table = fault switch { 2 => "server_bank_debts", 3 => "server_mercenary_contracts", _ => null };
            if (table is not null) Execute(database, $"create trigger fail_confirm before insert on {table} begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try { commits.CommitPendingOperations(new CrashStore(files, fault), bank, contracts, package, snapshot, batch); }
            catch (IOException) { failed = true; }
            catch (SqliteException) { failed = true; }
            Require(failed && Directory.EnumerateFiles(journal, "*.json").Count() == 1, "Confirmation failure lost its journal.");
            if (fault == 2)
                Require(bank.FindByIdempotencyKey("loan") == batch.Loan && Bank(database).FindByIdempotencyKey("loan") == batch.Loan
                    && bank.FindDebtByIdempotencyKey("debt") == batch.Debts.Single(), "Debt write failure did not roll back the loan batch.");
            if (fault == 3)
                Require(bank.FindByIdempotencyKey("loan") == PendingSnapshotOperations.Confirm(batch.Loan!, snapshot)
                    && contracts.Find("contract") == batch.Mercenaries.Single(), "Mercenary failure lost the committed bank state or leaked activation.");
            if (table is not null) Execute(database, "drop trigger fail_confirm;");

            // These records are deliberately inserted after the intent, bypassing the production coordinator.
            NewDebt(bank, "later", Accepted.AddMinutes(1));
            NewContract(contracts, "later", Accepted.AddMinutes(1));
            if (fault == 4) contracts.ReconcilePendingConfirmations("userA", "colonyA", Accepted, TimeSpan.Zero, true);
            if (fault == 5)
            {
                var newer = fixture("newer");
                files.StoreLatest(newer, newer.Index, Accepted.AddHours(1));
            }
            if (reopen)
            {
                bank = Bank(database);
                contracts = Contracts(database);
                files = new FileColonySnapshotIndexStore(snapshots);
                commits = new SnapshotCommitService(journal);
            }
            if (fault is 4 or 5)
            {
                bool rejected = false;
                try { commits.Recover(files, bank, (_, _) => { }, null, contracts: contracts); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected && Directory.EnumerateFiles(journal, "*.json").Any()
                    && files.GetLatest("userA", "colonyA")?.Identity.SnapshotId == (fault == 5 ? "newer" : null),
                    "Conflicting confirmation discarded its journal or overwrote a snapshot.");
                return;
            }
            for (int retry = 0; retry < 2; retry++)
                commits.Recover(files, bank, (_, _) => throw new Exception("Pure confirmation replay ran unrelated post-upload data."), null, contracts: contracts);
            Require(!Directory.EnumerateFiles(journal, "*.json").Any()
                && files.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == snapshot.Envelope.NextLineageToken,
                "Confirmation recovery lost the accepted snapshot or left its journal.");
            foreach (var current in new[] { bank, Bank(database) })
            {
                Require(current.FindByIdempotencyKey("loan") == PendingSnapshotOperations.Confirm(batch.Loan!, snapshot)
                    && current.FindDebtByIdempotencyKey("debt") == PendingSnapshotOperations.Confirm(batch.Debts.Single(), snapshot),
                    "Confirmation recovery changed the committed bank identity, clock or payment state.");
                Require(current.FindDebtByIdempotencyKey("later")!.Status == BankLoanRegistry.StatusPendingActivation,
                    "Recovery confirmed debt created after the fixed batch.");
            }
            foreach (var current in new[] { contracts, Contracts(database) })
                Require(current.Find("contract") == PendingSnapshotOperations.Confirm(batch.Mercenaries.Single(), snapshot)
                    && current.Find("later")!.Status == MercenaryContractRegistry.StatusPendingActivation,
                    "Recovery lost the captured mercenary or activated a later hire.");
        });
    }

    private static void VerifyPrimaryCommit(Func<string, SaveSnapshotPackage> fixture, bool legacy)
    {
        WithStorage((database, root) =>
        {
            var bank = Bank(database);
            var contracts = Contracts(database);
            Seed(bank, contracts, false);
            var package = fixture("primary");
            var files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
            var snapshot = new SnapshotUploadReceiver(files).Receive(new SnapshotUploadContext("userA", "colonyA", "primary"),
                package, Accepted, storeAccepted: false).AcceptedSnapshot!;
            var batch = PendingSnapshotOperations.Capture(bank, contracts, snapshot);
            var primary = PendingSnapshotOperations.Confirm(batch.Loan!, snapshot);
            string journal = Path.Combine(root, "intents");
            var commits = new SnapshotCommitService(journal);
            if (legacy)
            {
                var intent = new SnapshotCommitIntent(1, "legacy", null, null,
                    package with { Envelope = snapshot.Envelope, Index = snapshot.Index }, Accepted, primary, null, null);
                File.WriteAllText(Path.Combine(journal, "legacy.json"), JsonSerializer.Serialize(intent));
            }
            else
            {
                bool failed = false;
                try { commits.Commit(files, bank, package, snapshot, primary, null, (_, _) => throw new IOException("post-upload"), contracts: contracts); }
                catch (IOException) { failed = true; }
                Require(failed, "Primary post-upload fault was not injected.");
                contracts.Complete("contract", "userA", "colonyA");
            }
            NewDebt(bank, "later", Accepted.AddMinutes(1));
            NewContract(contracts, "later", Accepted.AddMinutes(1));
            commits = new SnapshotCommitService(journal);
            bank = Bank(database);
            contracts = Contracts(database);
            if (legacy)
            {
                try { commits.Recover(new CrashStore(files, 0), bank, (_, _) => { }, null, contracts: contracts); }
                catch (IOException) { }
                var upgraded = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllText(Path.Combine(journal, "legacy.json")))!;
                Require(upgraded.Version == 6 && upgraded.PendingOperations!.Loan is null && upgraded.PendingOperations.Debts.Count == 1
                    && upgraded.PendingOperations.Mercenaries.Count == 1, "Legacy recovery did not durably freeze its original batch.");
                // Even an old timestamp cannot add work to the already-upgraded intent.
                NewDebt(bank, "backdated", DateTimeOffset.UnixEpoch);
                commits = new SnapshotCommitService(journal);
            }
            int postUploads = 0;
            for (int retry = 0; retry < 2; retry++) commits.Recover(files, bank, (_, _) => postUploads++, null, contracts: contracts);
            Require(postUploads == 1 && bank.FindByIdempotencyKey("loan") == primary
                && bank.FindByIdempotencyKey("loan") == PendingSnapshotOperations.Confirm(batch.Loan!, snapshot)
                && bank.FindDebtByIdempotencyKey("debt") == PendingSnapshotOperations.Confirm(batch.Debts.Single(), snapshot)
                && bank.FindDebtByIdempotencyKey("later")!.Status == BankLoanRegistry.StatusPendingActivation
                && (!legacy || bank.FindDebtByIdempotencyKey("backdated")!.Status == BankLoanRegistry.StatusPendingActivation)
                && contracts.Find("contract")!.Status == (legacy ? MercenaryContractRegistry.StatusActive : MercenaryContractRegistry.StatusCompleted)
                && contracts.Find("later")!.Status == MercenaryContractRegistry.StatusPendingActivation,
                "Primary commit recovery rescanned new operations or reopened a completed contract.");
        });
    }

    private static void VerifyReceiveAndLifecycle(Func<string, SaveSnapshotPackage> fixture)
    {
        WithStorage((database, root) =>
        {
            string journal = Path.Combine(root, "intents"), snapshots = Path.Combine(root, "snapshots");
            ClashOfRimNetworkState Open() => new(snapshotStore: new FileColonySnapshotIndexStore(snapshots),
                bankLoans: Bank(database), mercenaryContracts: Contracts(database), snapshotCommits: new SnapshotCommitService(journal));
            var state = Open();
            Seed(state.BankLoans, state.MercenaryContracts, false);
            var package = fixture("ordinary");
            Require(!Receive(state, package with { Payload = [1] }).Accepted
                && state.SnapshotStore.GetLatest("userA", "colonyA") is null
                && state.BankLoans.FindByIdempotencyKey("loan")!.Status == BankLoanRegistry.StatusPendingActivation,
                "Invalid ordinary upload confirmed pending work.");
            Execute(database, "create trigger fail_confirm before insert on server_bank_debts begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try { Receive(state, package); }
            catch (SqliteException) { failed = true; }
            Require(failed && state.SnapshotStore.GetLatest("userA", "colonyA")?.Identity.SnapshotId == "ordinary",
                "Ordinary upload did not reach the snapshot-to-bank crash boundary.");
            Execute(database, "drop trigger fail_confirm;");
            state = Open();
            NewDebt(state.BankLoans, "later", DateTimeOffset.UtcNow);
            state.PendingConfirmations.ReconcileAfterRecovery("userA", "colonyA", DateTimeOffset.UtcNow, forceCancel: true);
            Require(state.BankLoans.FindByIdempotencyKey("loan")!.Status == BankLoanRegistry.StatusActive
                && state.BankLoans.FindDebtByIdempotencyKey("debt")!.Status == BankLoanRegistry.StatusActive
                && state.MercenaryContracts.Find("contract")!.Status == MercenaryContractRegistry.StatusActive
                && state.BankLoans.FindDebtByIdempotencyKey("later") is null
                && !Directory.EnumerateFiles(journal, "*.json").Any(), "Relogin cancelled work already owned by the snapshot journal.");
            NewDebt(state.BankLoans, "after-confirmation", DateTimeOffset.UtcNow);
            state.SnapshotPostUploads.AfterCommit(state.SnapshotStore.GetLatest("userA", "colonyA")!, null);
            Require(state.BankLoans.FindDebtByIdempotencyKey("after-confirmation")!.Status == BankLoanRegistry.StatusPendingActivation,
                "Delayed snapshot post-processing confirmed a later operation.");
        });
    }

    internal static SnapshotUploadResult Receive(ClashOfRimNetworkState state, SaveSnapshotPackage package) =>
        state.SnapshotReception.Receive(package.Envelope.Identity.OwnerId!, package.Envelope.Identity.ColonyId!,
            package.Envelope.Identity.SnapshotId!, ProtocolDtoMapper.ToMetadataDto(package), package.Payload, DateTimeOffset.UtcNow).Upload;

    internal static void AddAncillary(BankLoanRegistry bank) => NewDebt(bank, "ancillary", DateTimeOffset.UnixEpoch);
    internal static void VerifyAncillary(BankLoanRegistry bank, string snapshotId) =>
        Require(bank.FindDebtByIdempotencyKey("ancillary") is { Status: BankLoanRegistry.StatusActive } debt && debt.SnapshotId == snapshotId,
            "Business snapshot recovery lost its ancillary confirmation batch.");

    private static void Seed(BankLoanRegistry bank, MercenaryContractRegistry contracts, bool repayment)
    {
        bank.Create("loan", "userA", "colonyA", "before", 100, 10, 7, 1000, DateTimeOffset.UnixEpoch, "loan");
        NewDebt(bank, "debt", DateTimeOffset.UnixEpoch);
        NewContract(contracts, "contract", DateTimeOffset.UnixEpoch);
        if (!repayment) return;
        bank.ConfirmPendingForSnapshot("userA", "colonyA", "before", 1000, DateTimeOffset.UnixEpoch);
        bank.MarkRepaymentPending("loan-payment", "userA", "colonyA", 2000, DateTimeOffset.UnixEpoch);
        bank.MarkDebtRepaymentPending("debt-payment", "userA", "colonyA", bank.FindDebtByIdempotencyKey("debt")!.DebtId, 2000, DateTimeOffset.UnixEpoch);
    }
    private static void NewDebt(BankLoanRegistry bank, string key, DateTimeOffset at) => bank.CreateDebt(key,
        "userA", "colonyA", "before", 50, BankLoanRegistry.DebtSourceFine, "fine", "source", 1000, at, true);
    private static void NewContract(MercenaryContractRegistry contracts, string key, DateTimeOffset at) => contracts.Create(key,
        "userA", "colonyA", "before", "Shooting", 7, 7, 560, 1000, 1500, 1000, at, key);
    private static BankLoanRegistry Bank(string database) => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null);
    private static MercenaryContractRegistry Contracts(string database) => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryContracts), null);
    private static void WithStorage(Action<string, string> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-pending-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            run(database, root);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class CrashStore(IColonySnapshotPackageStore inner, int fault) : IColonySnapshotPackageStore
    {
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (fault is 0 or 4 or 5) throw new IOException("before snapshot");
            inner.StoreLatest(package, index, at);
            if (fault == 1) throw new IOException("after snapshot");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => throw new NotSupportedException();
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
