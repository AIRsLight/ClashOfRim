using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

internal static class MercenaryCommitCrashTests
{
    private static readonly ProtocolIdentity Buyer = new("userA", "colonyA", "before");

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool guard in new[] { false, true })
        {
            foreach (bool reopen in new[] { false, true })
            foreach (int fault in Enumerable.Range(0, 9)) VerifyRecovery(fixture, guard, reopen, fault);
            VerifyEndpoints(fixture, guard).GetAwaiter().GetResult();
            VerifyEndpointRecovery(fixture, guard).GetAwaiter().GetResult();
        }
    }

    private static void VerifyRecovery(Func<string, SaveSnapshotPackage> fixture, bool guard, bool reopen, int fault)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-mercenary-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var contracts = Contracts(database);
            var guards = Guards(database);
            string snapshotDirectory = Path.Combine(root, "snapshots"), journal = Path.Combine(root, "intents");
            var files = new FileColonySnapshotIndexStore(snapshotDirectory);
            var bank = new BankLoanRegistry();
            if (fault == 1) PendingOperationCommitCrashTests.AddAncillary(bank);
            var package = fixture("mercenary-commit");
            var snapshot = new SnapshotUploadReceiver(files).Receive(new SnapshotUploadContext("userA", "colonyA", "mercenary-commit"),
                package, DateTimeOffset.UnixEpoch, storeAccepted: false).AcceptedSnapshot!;
            var contract = Contract("hire", "contract", "mercenary-commit");
            var guardContract = Guard("hire", "contract", "mercenary-commit");
            var mutation = guard ? new MercenarySnapshotMutation(Guard: guardContract)
                : new MercenarySnapshotMutation(Contract: contract, MaximumOpenContracts: 1);
            var commits = new SnapshotCommitService(journal);
            if (fault == 2) Execute(database, $"create trigger fail_hire before insert on {(guard ? "server_mercenary_guard_contracts" : "server_mercenary_contracts")} begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try
            {
                commits.CommitMercenary(new CrashStore(files, fault), bank, contracts, guards, package, snapshot, mutation,
                    (_, _) => { if (fault is 3 or 7) throw new IOException("post-upload"); });
            }
            catch (IOException) { failed = true; }
            catch (SqliteException) when (fault == 2) { failed = true; }
            Require(failed, "Mercenary fault was not injected.");
            if (fault == 2)
            {
                Require(contracts.CountOpenForColony("userA", "colonyA") == 0 && guards.FindActiveForColony("userA", "colonyA") is null,
                    "Failed contract persistence leaked active state.");
                Execute(database, "drop trigger fail_hire;");
            }
            if (fault is 4 or 5 or 8)
            {
                string key = fault == 5 ? "hire" : "other-key", id = fault == 4 ? "contract" : "other-contract";
                if (guard) guards.ActivateWithSnapshot(key, "userA", "colonyA", id, "other-snapshot", "Master", 300, 1, 1000, DateTimeOffset.UnixEpoch);
                else contracts.Create(key, "userA", "colonyA", "other-snapshot", "Shooting", 14, 7, 300, 200, 100, 1000, DateTimeOffset.UnixEpoch, id);
            }
            if (fault == 6)
            {
                var newer = fixture("newer");
                files.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
            }
            if (fault == 7)
            {
                if (guard)
                {
                    guards.ConsumeForRaid("userA", "colonyA", "raid", "contract", DateTimeOffset.UnixEpoch);
                    guards.ActivateWithSnapshot("replacement", "userA", "colonyA", "replacement", "mercenary-commit", "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UnixEpoch);
                }
                else
                {
                    contracts.Complete("contract", "userA", "colonyA");
                    contracts.ActivateWithSnapshot("replacement", "userA", "colonyA", "replacement", "mercenary-commit", "Shooting", 7, 7, 560, 1000, 1500, 1000, DateTimeOffset.UnixEpoch);
                }
            }
            if (reopen)
            {
                contracts = Contracts(database);
                guards = Guards(database);
                files = new FileColonySnapshotIndexStore(snapshotDirectory);
                commits = new SnapshotCommitService(journal);
            }
            if (fault is 4 or 5 or 6 or 8)
            {
                bool rejected = false;
                try { commits.Recover(files, bank, (_, _) => { }, null, contracts: contracts, guards: guards); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected && Directory.EnumerateFiles(journal, "*.json").Any()
                    && files.GetLatest("userA", "colonyA")?.Identity.SnapshotId == (fault == 6 ? "newer" : null),
                    "Conflicting mercenary recovery overwrote state or discarded the journal.");
                return;
            }
            int processed = 0;
            commits.Recover(files, bank, (_, _) => processed++, null, contracts: contracts, guards: guards);
            commits.Recover(files, bank, (_, _) => processed++, null, contracts: contracts, guards: guards);
            Require(processed == 1 && !Directory.EnumerateFiles(journal, "*.json").Any()
                && files.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == snapshot.Envelope.NextLineageToken,
                "Mercenary recovery lost its snapshot or did not finish.");
            if (fault == 1) PendingOperationCommitCrashTests.VerifyAncillary(bank, "mercenary-commit");
            if (guard)
            {
                var expected = fault == 7 ? guardContract with { Status = MercenaryGuardContractRegistry.StatusConsumed, ConsumedRaidEventId = "raid", ConsumedAtUtc = DateTimeOffset.UnixEpoch } : guardContract;
                Require(guards.FindByIdempotencyKey("hire") == expected && Guards(database).FindByIdempotencyKey("hire") == expected
                    && guards.FindActiveForColony("userA", "colonyA")?.ContractId == (fault == 7 ? "replacement" : "contract"),
                    "Guard recovery duplicated or reactivated a consumed deployment.");
            }
            else
            {
                var expected = fault == 7 ? contract with { Status = MercenaryContractRegistry.StatusCompleted } : contract;
                Require(contracts.FindByIdempotencyKey("hire") == expected && Contracts(database).FindByIdempotencyKey("hire") == expected
                    && contracts.CountOpenForColony("userA", "colonyA") == 1, "Mercenary recovery duplicated or reopened a completed contract.");
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyEndpoints(Func<string, SaveSnapshotPackage> fixture, bool guard)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var state = new ClashOfRimNetworkState(serverConfiguration: Configuration());
        var package = PrepareEndpoint(state, fixture, Buyer, "paid");
        var accepted = await Invoke(state, package, Buyer, guard);
        Require(accepted.Result.Accepted && accepted.SnapshotId == "paid" && !string.IsNullOrWhiteSpace(accepted.Token),
            "Hire endpoint rejected: " + JsonSerializer.Serialize(accepted));
        var replay = await Invoke(state, package, Buyer, guard);
        Require(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.ContractId == accepted.ContractId
            && replay.SnapshotId == accepted.SnapshotId && replay.Token == accepted.Token, "Hire replay lost its confirmed snapshot.");
        foreach (string changed in new[] { "id", "price", "terms", "snapshot" })
            Require(!(await Invoke(state, package, Buyer, guard, change: changed)).Result.Accepted, "Hire replay accepted changed " + changed);
        var other = new ProtocolIdentity("other", "other-colony", "before");
        var otherPackage = PrepareEndpoint(state, fixture, other, "paid");
        Require(!(await Invoke(state, otherPackage, other, guard)).Result.Accepted, "Hire replay accepted another owner.");
        Require((await Invoke(state, package, Buyer, guard, principal: other)).Result.ErrorCode == ProtocolErrorCode.Unauthorized,
            "Hire endpoint accepted a mismatched multipart principal.");
        if (guard) state.MercenaryGuards.ConsumeForRaid("userA", "colonyA", "raid", "contract", DateTimeOffset.UtcNow);
        else state.MercenaryContracts.Complete("contract", "userA", "colonyA");
        replay = await Invoke(state, package, Buyer, guard);
        Require(replay.Result.Accepted && (guard ? state.MercenaryGuards.FindByIdempotencyKey("hire")!.Status == MercenaryGuardContractRegistry.StatusConsumed
            : state.MercenaryContracts.FindByIdempotencyKey("hire")!.Status == MercenaryContractRegistry.StatusCompleted),
            "Hire retry reopened a terminal contract.");

        // A shared contract ID cannot spend a second colony's snapshot.
        var collision = await Invoke(state, otherPackage, other, guard, key: "different-key");
        Require(!collision.Result.Accepted && state.SnapshotStore.GetLatest(other.UserId, other.ColonyId!)!.Identity.SnapshotId == "before",
            "Contract ID conflict published the second colony's paid snapshot.");

        var invalid = new ClashOfRimNetworkState(serverConfiguration: Configuration());
        var invalidPackage = PrepareEndpoint(invalid, fixture, Buyer, "invalid");
        Require(!(await Invoke(invalid, invalidPackage with { Payload = new byte[] { 1 } }, Buyer, guard)).Result.Accepted
            && invalid.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "before", "Invalid hire payload published a snapshot.");

        if (!guard)
        {
            invalid.MercenaryContracts.Create("pending", "userA", "colonyA", "before", "Shooting", 7, 7, 560, 1000, 1500,
                1000, DateTimeOffset.UtcNow, "pending-contract");
            Require(!(await Invoke(invalid, invalidPackage, Buyer, guard)).Result.Accepted, "Pending legacy contract did not reserve the last mercenary slot.");
        }

        var raced = new ClashOfRimNetworkState(serverConfiguration: Configuration());
        var candidate = PrepareEndpoint(raced, fixture, Buyer, "race");
        raced.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out var session);
        string token = raced.AuthTokens.Issue("userA", "userA", "colonyA", session, DateTimeOffset.UtcNow);
        var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(() => Invoke(raced,
            candidate with { Envelope = candidate.Envelope with { Identity = new SnapshotIdentity("userA", "colonyA", "race-" + i) } },
            Buyer, guard, key: "race-" + i, contractId: "contract-" + i, token: token)));
        var results = await Task.WhenAll(attempts);
        Require(results.Count(r => r.Result.Accepted) == 1
            && (guard ? raced.MercenaryGuards.FindActiveForColony("userA", "colonyA") is not null
                : raced.MercenaryContracts.CountOpenForColony("userA", "colonyA") == 1), "Concurrent hires exceeded colony capacity.");
        var winner = results.Single(r => r.Result.Accepted);
        Require(raced.SnapshotStore.GetLatest("userA", "colonyA")!.Identity.SnapshotId == winner.SnapshotId, "Rejected hire overwrote the winner's snapshot.");
    }

    private static async Task VerifyEndpointRecovery(Func<string, SaveSnapshotPackage> fixture, bool guard)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-mercenary-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            ClashOfRimNetworkState Open() => new(snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")),
                serverConfiguration: Configuration(), mercenaryContracts: Contracts(database), mercenaryGuards: Guards(database),
                snapshotCommits: new SnapshotCommitService(Path.Combine(root, "intents")));
            var state = Open();
            var package = PrepareEndpoint(state, fixture, Buyer, "recovered");
            Execute(database, $"create trigger fail_hire before insert on {(guard ? "server_mercenary_guard_contracts" : "server_mercenary_contracts")} begin select raise(abort, 'injected failure'); end;");
            bool failed = false;
            try { await Invoke(state, package, Buyer, guard); } catch (SqliteException) { failed = true; }
            Require(failed, "Endpoint persistence failure was not injected.");
            string? acceptedToken = state.SnapshotStore.GetLatest("userA", "colonyA")?.Envelope.NextLineageToken;
            Execute(database, "drop trigger fail_hire;");
            state = Open();
            var result = await Invoke(state, package, Buyer, guard);
            Require(result.Result.Accepted && result.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
                && result.ContractId == "contract" && result.SnapshotId == "recovered" && result.Token == acceptedToken
                && !Directory.EnumerateFiles(Path.Combine(root, "intents"), "*.json").Any(), "Hire endpoint failed to recover and replay after restart.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task<Reply> Invoke(ClashOfRimNetworkState state, SaveSnapshotPackage package, ProtocolIdentity owner,
        bool guard, string key = "hire", string contractId = "contract", string? change = null, ProtocolIdentity? principal = null, string? token = null)
    {
        var metadata = ProtocolDtoMapper.ToMetadataDto(change == "snapshot"
            ? package with { Envelope = package.Envelope with { Identity = new SnapshotIdentity(owner.UserId, owner.ColonyId!, "other-snapshot") } } : package);
        if (guard)
        {
            var response = await SnapshotEndpointTestClient.Invoke<MercenaryGuardHireResponse>("HireMercenaryGuardWithSnapshot", state,
                auth => new HireMercenaryGuardWithSnapshotRequest(key, owner.UserId, owner.ColonyId!, "before", auth, 1000,
                    change == "id" ? "changed-id" : contractId, change == "terms" ? "Master" : "Apprentice", change == "price" ? 601 : 600, 0.35f, metadata),
                package.Payload, principal ?? owner, token);
            return new Reply(response.Result, response.Contract?.ContractId, response.AppliedSnapshotId, response.NextLineageToken);
        }
        else
        {
            var response = await SnapshotEndpointTestClient.Invoke<MercenaryHireResponse>("HireMercenaryWithSnapshot", state,
                auth => new HireMercenaryWithSnapshotRequest(key, owner.UserId, owner.ColonyId!, "before", auth, 1000,
                    change == "terms" ? "Construction" : "Shooting", 7, 7, change == "id" ? "changed-id" : contractId,
                    change == "price" ? 561 : 560, 1000, 1500, metadata), package.Payload, principal ?? owner, token);
            return new Reply(response.Result, response.Contract?.ContractId, response.AppliedSnapshotId, response.NextLineageToken);
        }
    }

    private static SaveSnapshotPackage PrepareEndpoint(ClashOfRimNetworkState state, Func<string, SaveSnapshotPackage> fixture,
        ProtocolIdentity owner, string snapshotId)
    {
        var baseline = fixture("before");
        baseline = baseline with { Envelope = baseline.Envelope with { Identity = new SnapshotIdentity(owner.UserId, owner.ColonyId!, "before") } };
        var accepted = new SnapshotUploadReceiver(state.SnapshotStore).Receive(new SnapshotUploadContext(owner.UserId, owner.ColonyId!, "before"),
            baseline, DateTimeOffset.UtcNow).AcceptedSnapshot!;
        using var compressed = new MemoryStream(baseline.Payload);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        var document = XDocument.Load(gzip);
        document.Root!.Element("game")!.Add(new XElement("components", new XElement("li",
            new XAttribute("Class", "AIRsLight.ClashOfRim.ClashOfRimGameComponent"),
            new XElement("clashOfRimLineageSnapshotId", "before"), new XElement("clashOfRimLineageToken", accepted.Envelope.NextLineageToken))));
        string path = Path.Combine(Path.GetTempPath(), "cor-hire-child-" + Guid.NewGuid().ToString("N") + ".rws");
        try
        {
            document.Save(path);
            return SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity(owner.UserId, owner.ColonyId!, snapshotId), DateTimeOffset.UtcNow);
        }
        finally { File.Delete(path); }
    }

    private static MercenaryContractRecord Contract(string key, string id, string snapshot) => new(id, key, "userA", "colonyA", snapshot,
        "Shooting", 7, 7, 560, 1000, 1500, 1000, 1000 + 7 * BankLoanPolicy.GameTicksPerDay, DateTimeOffset.UnixEpoch,
        MercenaryContractRegistry.StatusActive, DateTimeOffset.UnixEpoch);
    private static MercenaryGuardContractRecord Guard(string key, string id, string snapshot) => new(id, key, "userA", "colonyA", snapshot,
        "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UnixEpoch, MercenaryGuardContractRegistry.StatusActive, null, null);
    private static ClashOfRimServerConfiguration Configuration() => new(pvpEnabled: true, maxActiveMercenariesPerColony: 1);
    private static MercenaryContractRegistry Contracts(string database) => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryContracts), null);
    private static MercenaryGuardContractRegistry Guards(string database) => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryGuards), null);
    private sealed record Reply(ProtocolResponse Result, string? ContractId, string? SnapshotId, string? Token);
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
            if (fault is 0 or 4 or 5 or 6 or 8) throw new IOException("before snapshot");
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
