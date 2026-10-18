using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Xml.Linq;

internal static class MercenaryIncidentRecoveryTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool reopen in new[] { false, true })
        foreach (string kind in new[] { "Death", "ShuttleDestroyed", "HarmfulSurgery", "HostileBehavior", "OvertimeService" })
        foreach (int fault in Enumerable.Range(MercenaryIncidentRecovery.ClosesContract("Mercenary:" + kind) ? 0 : 1,
                     MercenaryIncidentRecovery.ClosesContract("Mercenary:" + kind) ? 4 : 3))
            VerifyRecovery(kind, fault, reopen);
        VerifyEndpoint(fixture);
        VerifyConflicts();
    }

    private static void VerifyRecovery(string kind, int fault, bool reopen)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-incident-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            ClashOfRimNetworkState Open() => new(ledger: new SqliteAuthoritativeEventLedger(database),
                bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null),
                mercenaryContracts: new MercenaryContractRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryContracts), null));
            var state = Open();
            SeedContract(state);
            var debt = state.BankLoans.CreateDebt("incident", "user", "colony", "confirmed", 1500,
                BankLoanRegistry.DebtSourceFine, "Mercenary:" + kind, "contract", 1000, DateTimeOffset.UnixEpoch);
            string? table = fault switch { 0 => "server_mercenary_contracts", 1 => "events", 2 => "server_bank_debts", _ => null };
            if (table is not null) Execute(database, $"create trigger fail_incident before insert on {table} begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try { Recover(state, _ => { if (fault == 3) throw new IOException("notification wake-up"); }); }
            catch (SqliteException) { failed = true; }
            catch (IOException) { failed = true; }
            Require(failed && !state.BankLoans.FindDebtByIdempotencyKey("incident")!.MercenaryIncidentFinalized,
                "Failed incident processing was incorrectly marked complete.");
            string? notificationId = state.Ledger.FindByIdempotencyKey("bank-fine-created:" + debt.DebtId)?.EventId;
            if (table is not null) Execute(database, "drop trigger fail_incident;");
            if (fault == 3) state.BankLoans.MarkDebtRepaidWithSnapshot("payment", "user", "colony", debt.DebtId,
                "paid", debt.AmountSilver, 2000, DateTimeOffset.UnixEpoch);
            if (reopen) state = Open();
            int wakes = 0;
            Recover(state, _ => wakes++);
            Recover(state, _ => wakes++);
            var stored = Open();
            var finalDebt = stored.BankLoans.FindDebtByIdempotencyKey("incident")!;
            Require(finalDebt.MercenaryIncidentFinalized && finalDebt.DebtId == debt.DebtId
                && finalDebt.Status == (fault == 3 ? BankLoanRegistry.StatusRepaid : BankLoanRegistry.StatusActive)
                && (fault != 3 || finalDebt.RepaymentIdempotencyKey == "payment" && finalDebt.SnapshotId == "paid"),
                "Incident retry lost repayment state or created a second fine.");
            string expectedStatus = MercenaryIncidentRecovery.ClosesContract(debt.Reason)
                ? MercenaryContractRegistry.StatusCompleted : MercenaryContractRegistry.StatusActive;
            Require(stored.MercenaryContracts.Find("contract")!.Status == expectedStatus
                && stored.Ledger.ListAll().Count == 1 && wakes == 1
                && (notificationId is null || stored.Ledger.ListAll().Single().EventId == notificationId),
                "Incident retry lost contract closure, duplicated a notification or repeated finalized work.");
            Recover(stored, _ => throw new Exception("Finalized work ran again after reopen."));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static void VerifyEndpoint(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool confirm in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState();
            SeedContract(state);
            StoreSnapshot(state, fixture("before"), "before");
            string token = Token(state, "user", "colony");
            var first = Report(state, "incident", "Death", token);
            Require(first.Result.Accepted && first.Debt is not null
                && state.MercenaryContracts.FindActive("contract") is not null && state.Ledger.ListAll().Count == 0,
                "Unconfirmed fine closed a contract or published a notification.");
            var repeated = Report(state, "incident", "Death", token);
            Require(repeated.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && repeated.Debt!.DebtId == first.Debt!.DebtId,
                "Pending incident retry created another fine.");
            Require(Report(state, "second-death", "Death", token).Result.ErrorCode == ProtocolErrorCode.Conflict
                && Report(state, "complete", "Completed", token).Result.ErrorCode == ProtocolErrorCode.Conflict,
                "Pending terminal incident allowed a second fine or premature completion.");
            foreach (var changed in new[] { ("HarmfulSurgery", "contract"), ("Death", "other-contract"), ("Completed", "contract") })
                Require(Report(state, "incident", changed.Item1, token, contractId: changed.Item2).Result.ErrorCode == ProtocolErrorCode.Conflict,
                    "Incident key was replayed with different business parameters.");
            StoreSnapshot(state, fixture("other"), "other", "other", "other-colony");
            string otherToken = Token(state, "other", "other-colony");
            Require(Report(state, "incident", "Death", otherToken, user: "other", colony: "other-colony", snapshot: "other").Result.ErrorCode == ProtocolErrorCode.Conflict,
                "Incident key disclosed another user's debt.");
            if (!confirm)
            {
                state.BankLoans.ReconcilePendingConfirmations("user", "colony", DateTimeOffset.UtcNow, TimeSpan.Zero, true);
                state.SnapshotMutations.Recover();
                Require(state.BankLoans.FindDebtByIdempotencyKey("incident") is null && state.MercenaryContracts.FindActive("contract") is not null
                    && state.Ledger.ListAll().Count == 0, "Cancelling an unconfirmed fine left irreversible side effects.");
                continue;
            }
            var package = fixture("confirmed");
            using (var compressed = new MemoryStream(package.Payload))
            using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            {
                var document = XDocument.Load(gzip);
                document.Root!.Add(new XComment("Incident confirmation save"));
                string path = Path.Combine(Path.GetTempPath(), "cor-incident-confirm-" + Guid.NewGuid().ToString("N") + ".rws");
                try
                {
                    document.Save(path);
                    package = SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity("user", "colony", "confirmed"), DateTimeOffset.UtcNow);
                }
                finally { File.Delete(path); }
            }
            var upload = PendingOperationCommitCrashTests.Receive(state, package);
            Require(upload.Accepted, "Incident confirmation snapshot was rejected: " + upload);
            Require(state.BankLoans.FindDebtByIdempotencyKey("incident") is { Status: BankLoanRegistry.StatusActive, MercenaryIncidentFinalized: true }
                && state.MercenaryContracts.Find("contract")!.Status == MercenaryContractRegistry.StatusCompleted
                && state.Ledger.ListAll().Count == 1, "Confirmed snapshot did not finalize the fine and contract.");
            repeated = Report(state, "incident", "Death", token, snapshot: "confirmed");
            Require(repeated.Result.Accepted && repeated.Debt!.DebtId == first.Debt!.DebtId && state.Ledger.ListAll().Count == 1,
                "Confirmed incident retry changed the fine.");
            var debt = state.BankLoans.FindDebtByIdempotencyKey("incident")!;
            state.BankLoans.MarkDebtRepaidWithSnapshot("payment", "user", "colony", debt.DebtId, "confirmed", debt.AmountSilver, 1000, DateTimeOffset.UtcNow);
            Require(Report(state, "payment", "Death", token, snapshot: "confirmed").Result.ErrorCode == ProtocolErrorCode.Conflict,
                "A repayment key was accepted as an incident key.");
        }
    }

    private static void VerifyConflicts()
    {
        foreach (bool wrongContract in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState();
            SeedContract(state);
            var debt = state.BankLoans.CreateDebt("incident", wrongContract ? "other" : "user", "colony", "confirmed", 1500,
                BankLoanRegistry.DebtSourceFine, "Mercenary:Death", "contract", 1000, DateTimeOffset.UnixEpoch);
            if (!wrongContract) state.Ledger.Append(Notification(debt) with { Target = new EventParty("other", "other-colony") });
            bool failed = false;
            try { Recover(state, _ => { }); } catch (InvalidDataException) { failed = true; }
            Require(failed && !state.BankLoans.FindDebtByIdempotencyKey("incident")!.MercenaryIncidentFinalized
                && state.MercenaryContracts.FindActive("contract") is not null, "Conflicting incident finalized an unrelated contract or notification.");
        }
    }

    private static void Recover(ClashOfRimNetworkState state, Action<string> signal) => MercenaryIncidentRecovery.Recover(
        state.BankLoans, state.MercenaryContracts, state.Ledger, Notification, signal);
    private static AuthoritativeEvent Notification(BankDebtRecord debt) => AuthoritativeEventFactory.Create(
        ServerEventType.ServerNotification, new EventParty("server"), new EventParty(debt.UserId, debt.ColonyId), "bank-fine-created:" + debt.DebtId,
        false, new ServerNotificationEventPayload("bank-fine-created:" + debt.DebtId, "Fine", "Fine confirmed", ServerNotificationSeverity.Warning, false), debt.CreatedAtUtc);
    private static void SeedContract(ClashOfRimNetworkState state) => state.MercenaryContracts.ActivateWithSnapshot("hire", "user", "colony", "contract", "before",
        "Shooting", 7, 7, 560, 1000, 1500, 1000, DateTimeOffset.UtcNow);
    private static LatestSnapshotRecord StoreSnapshot(ClashOfRimNetworkState state, SaveSnapshotPackage package, string id,
        string user = "user", string colony = "colony")
    {
        var identity = new SnapshotIdentity(user, colony, id);
        var latest = new LatestSnapshotRecord(identity, package.Envelope with { Identity = identity }, package.Index, DateTimeOffset.UtcNow);
        state.SnapshotStore.StoreLatest(latest);
        return latest;
    }
    private static string Token(ClashOfRimNetworkState state, string user, string colony)
    {
        state.LoginSessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out string session);
        return state.AuthTokens.Issue(user, user, colony, session, DateTimeOffset.UtcNow);
    }
    private static MercenaryIncidentResponse Report(ClashOfRimNetworkState state, string key, string kind, string token,
        string user = "user", string colony = "colony", string snapshot = "before", string contractId = "contract") =>
        state.Mercenaries.ReportMercenaryIncident(new ReportMercenaryIncidentRequest(key, user, colony, snapshot, token, 1000, contractId, kind));
    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
