using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class BankCommitCrashTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (int crashAt in new[] { 0, 1, 2, 3 })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-bank-commit-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                var bank = CreateBank(database, seed: true);
                var files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
                var store = new CrashStore(files, crashAt);
                SaveSnapshotPackage package = fixture("bank-commit");
                LatestSnapshotRecord snapshot = new SnapshotUploadReceiver(files).Receive(
                    new SnapshotUploadContext("userA", "colonyA", "bank-commit"), package,
                    DateTimeOffset.UnixEpoch, storeAccepted: false).AcceptedSnapshot!;
                BankLoanRecord repaid = bank.GetActive("userA", "colonyA")! with
                {
                    Status = BankLoanRegistry.StatusRepaid, SnapshotId = "bank-commit", RepaymentIdempotencyKey = "repayment"
                };
                var commits = new BankSnapshotCommitService(Path.Combine(root, "intents"));
                if (crashAt == 3) Execute(database,
                    "create trigger fail_bank_commit before insert on server_bank_loans begin select raise(abort, 'injected bank persistence failure'); end;");
                bool failed = false;
                try
                {
                    commits.Commit(store, bank, package, snapshot, repaid, null,
                        (_, _) => { if (crashAt == 2) throw new IOException("injected post-upload crash"); });
                }
                catch (IOException) { failed = true; }
                catch (SqliteException) when (crashAt == 3) { failed = true; }
                if (!failed) throw new Exception("Fault was not injected.");
                if (crashAt == 3)
                {
                    if (bank.GetActive("userA", "colonyA") is null)
                        throw new Exception("A failed bank write leaked into the in-memory cache.");
                    Execute(database, "drop trigger fail_bank_commit;");
                }

                // Reopen both durable stores; no in-memory cache survives the simulated crash.
                bank = CreateBank(database, seed: false);
                files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
                commits = new BankSnapshotCommitService(Path.Combine(root, "intents"));
                int processed = 0;
                commits.Recover(files, bank, (_, _) => processed++);
                commits.Recover(files, bank, (_, _) => processed++);
                if (BankSnapshotReplayPolicy.FindLoanSnapshot(bank, "repayment", "userA", "colonyA", "bank-commit", true) != "bank-commit"
                    || BankSnapshotReplayPolicy.FindLoanSnapshot(bank, "repayment", "other", "colonyA", "bank-commit", true) is not null
                    || BankSnapshotReplayPolicy.FindLoanSnapshot(bank, "borrow", "userA", "colonyA", "bank-commit", true) is not null
                    || BankSnapshotReplayPolicy.FindLoanSnapshot(bank, "repayment", "userA", "colonyA", "different-snapshot", true) is not null)
                    throw new Exception("Committed replay must bind owner, operation and confirmed snapshot.");
                if (bank.GetActive("userA", "colonyA") is not null
                    || bank.FindByIdempotencyKey("repayment")?.Status != BankLoanRegistry.StatusRepaid
                    || files.GetLatest("userA", "colonyA")?.Envelope.NextLineageToken != snapshot.Envelope.NextLineageToken
                    || !files.GetLatestPackage("userA", "colonyA")!.Payload.SequenceEqual(package.Payload)
                    || processed != 1 || Directory.EnumerateFiles(Path.Combine(root, "intents"), "*.json").Any())
                    throw new Exception("Bank recovery was incomplete or not idempotent.");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static BankLoanRegistry CreateBank(string database, bool seed)
    {
        var bank = new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null);
        if (!seed) return bank;
        bank.Create("borrow", "userA", "colonyA", "before", 100, 10, 1, 0, DateTimeOffset.UnixEpoch, "loan");
        bank.ConfirmPendingForSnapshot("userA", "colonyA", "before", 0, DateTimeOffset.UnixEpoch);
        return bank;
    }

    private sealed class CrashStore(IColonySnapshotPackageStore inner, int crashAt) : IColonySnapshotPackageStore
    {
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (crashAt == 0) throw new IOException("injected pre-snapshot crash");
            inner.StoreLatest(package, index, at);
            if (crashAt == 1) throw new IOException("injected post-snapshot crash");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => throw new NotSupportedException();
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
