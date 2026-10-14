using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ColonyDeletionRecoveryTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool admin in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "before", "after", "world", "diplomacy", "bank", "player", "protection", "mercenary", "event", "status", "ack" })
        {
            if (fault == "ack" && !OperatingSystem.IsWindows()) continue;
            WithFixture(fixture, f => VerifyRecovery(f, admin, reopen, fault));
        }
        foreach (bool reopen in new[] { false, true })
        foreach (string conflict in new[] { "snapshot", "token", "hash", "source" })
            WithFixture(fixture, f => VerifyConflict(f, reopen, conflict));
        WithFixture(fixture, VerifyBackground);
        WithFixture(fixture, VerifyJournalFailure);
        WithFixture(fixture, VerifyLateSiteRegistration);
        WithFixture(fixture, VerifyNestedMutation);
        WithFixture(fixture, VerifyJournalValidation);
        foreach (bool admin in new[] { false, true })
            WithFixture(fixture, f => VerifyLegacyIdentityRecovery(f, admin));
    }

    private static void VerifyLegacyIdentityRecovery(Fixture f, bool admin)
    {
        f.Store.Fault = "before";
        Fails(() => f.Delete(admin));
        string path = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        var intent = JsonSerializer.Deserialize<ColonyDeletionIntent>(File.ReadAllBytes(path))!;
        string newId = intent.Tombstone.EventId;
        var legacy = intent.Tombstone with
        {
            EventId = AuthoritativeEventFactory.BuildLegacyEventId(intent.Tombstone.Type, intent.Tombstone.IdempotencyKey)
        };
        intent = intent with { Batch = intent.Batch with { Events = new[] { legacy } } };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(intent));
        f.Open();
        f.Recover();
        VerifyCompleted(f, intent);
        Require(f.State.Ledger.Find(newId) is null, "legacy deletion recovery generated a second tombstone identity");
        f.Open();
        f.Delete(admin: true);
        VerifyCompleted(f, intent);
        Require(f.State.Ledger.Find(newId) is null, "repeated deletion replaced the legacy tombstone identity");
    }

    private static void VerifyRecovery(Fixture f, bool admin, bool reopen, string fault)
    {
        string? sql = fault switch
        {
            "world" => "before insert on server_keyed_json_records when new.item_key='player-colony-sites'",
            "diplomacy" => "before delete on server_diplomacy_relations",
            "bank" => "before delete on server_bank_loans",
            "player" => "before insert on server_player_colony_tombstones",
            "protection" => "before delete on server_raid_protection_activations",
            "mercenary" => "before insert on server_mercenary_contracts",
            "event" => "before insert on events",
            "status" => "before update on events when old.event_id='memo'",
            _ => null
        };
        if (sql is not null) f.Sql("create trigger fail_delete " + sql + " begin select raise(abort, 'injected deletion failure'); end;");
        f.Store.Fault = fault;
        using (var journalLock = new DeferredFileLock())
        {
            if (fault == "ack") f.Store.BeforeRemove = () => journalLock.Lock(Directory.EnumerateFiles(f.Journal, "*.json").Single());
            Fails(() => f.Delete(admin));
        }
        string journal = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        var intent = JsonSerializer.Deserialize<ColonyDeletionIntent>(File.ReadAllBytes(journal))!;
        Require(intent.Previous == f.Package.Envelope && intent.Batch.Events.Count == 1 && intent.Sources.Count == 2
            && intent.NotifyUsers.Contains("buyer") && f.State.ColonyDeletions.HasPending, "deletion intent lost baseline, source graph or notification users");
        if (fault is "event" or "status") Require(f.State.Ledger.Find("order")!.Status == ServerEventStatus.Recorded
            && f.State.Ledger.Find("memo")!.Status == ServerEventStatus.Recorded && f.State.Ledger.ListAll().Count == 2,
            "closure failure partially published the tombstone or event statuses");
        if (sql is not null) f.Sql("drop trigger fail_delete;");
        f.Store.Fault = null;
        f.Store.BeforeRemove = null;
        if (reopen) f.Open();
        long notified = f.State.EventNotifications.GetVersion("buyer");
        f.Recover();
        VerifyCompleted(f, intent);
        Require(f.State.EventNotifications.GetVersion("buyer") > notified, "recovery forgot the buyer after the order was already closed");
        long after = f.State.EventNotifications.GetVersion("buyer");
        f.Recover();
        Require(f.State.EventNotifications.GetVersion("buyer") == after, "completed intent ran again");
        f.Open();
        VerifyCompleted(f, intent);
        // Repeating an administrative delete must preserve the original tombstone, including its timestamp.
        f.Delete(admin: true);
        VerifyCompleted(f, intent);
    }

    private static void VerifyConflict(Fixture f, bool reopen, string conflict)
    {
        f.Store.Fault = "before";
        Fails(() => f.Delete(admin: false));
        f.Store.Fault = null;
        if (conflict == "source")
        {
            var source = f.State.Ledger.Find("order")!;
            var changed = ((TradeEventPayload)source.Payload) with { TradeId = "changed" };
            f.State.Ledger.ApplyBatch(new LedgerBatch([], [new(source.EventId, source.Status, source.Status, source.Payload, changed)]));
        }
        else
        {
            var envelope = conflict switch
            {
                "snapshot" => f.Package.Envelope with { Identity = f.Package.Envelope.Identity with { SnapshotId = "newer" } },
                "token" => f.Package.Envelope with { NextLineageToken = "new-token" },
                _ => f.Package.Envelope with { PayloadSha256 = "changed-hash" }
            };
            f.Files.StoreLatest(f.Package with { Envelope = envelope }, f.Package.Index, f.Now);
        }
        var before = f.Files.GetLatest("userA", "colonyA")!.Envelope;
        if (reopen) f.Open();
        Fails(f.Recover);
        Require(f.State.ColonyDeletions.HasPending && Directory.EnumerateFiles(f.Journal, "*.json").Count() == 1
            && f.Files.GetLatest("userA", "colonyA")!.Envelope == before && !f.State.Players.IsDeleted("userA", "colonyA"),
            "conflicting deletion discarded intent or removed a changed snapshot");
    }

    private static void VerifyBackground(Fixture f)
    {
        f.Store.Fault = "after";
        Fails(() => f.Delete(admin: true));
        var intent = JsonSerializer.Deserialize<ColonyDeletionIntent>(File.ReadAllBytes(Directory.EnumerateFiles(f.Journal, "*.json").Single()))!;
        f.Open();
        using var background = new SnapshotPostUploadBackgroundService(f.State, new SnapshotPostUploadProcessorSource(() => []));
        background.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Require(SpinWait.SpinUntil(() => !f.State.ColonyDeletions.HasPending, TimeSpan.FromSeconds(10)), "background did not finish deletion without another request");
            VerifyCompleted(f, intent);
        }
        finally { background.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private static void VerifyJournalFailure(Fixture f)
    {
        // A broken journal destination must fail before the first destructive operation.
        Directory.Delete(f.Journal);
        File.WriteAllText(f.Journal, "unavailable journal directory");
        Fails(() => f.Delete(admin: false));
        Require(f.Files.GetLatest("userA", "colonyA")?.Envelope == f.Package.Envelope
            && !f.State.Players.IsDeleted("userA", "colonyA") && f.State.BankLoans.FindByIdempotencyKey("loan") is not null,
            "deletion changed state without a durable intent");
        File.Delete(f.Journal);
        Directory.CreateDirectory(f.Journal);
        f.Delete(admin: false);
        Require(f.Files.GetLatest("userA", "colonyA") is null, "journal failure prevented a later valid deletion");
    }

    private static void VerifyLateSiteRegistration(Fixture f)
    {
        f.Store.Fault = "after";
        Fails(() => f.Delete(admin: false));
        f.Store.Fault = null;
        var request = new RegisterPlayerColonySitesRequest(ProtocolApiVersion.Current, "userA", "colonyA",
            [new PlayerColonySiteDto("userA", "colonyA", "late", "late", 1, "late request")]);
        var result = (IValueHttpResult)Invoke("RegisterPlayerColonySites", request, f.State, NullLoggerFactory.Instance)!;
        Require(!((RegisterPlayerColonySitesResponse)result.Value!).Result.Accepted && !f.State.ColonyDeletions.HasPending
            && f.State.WorldConfiguration.Current!.PlayerColonySites.All(site => site.ColonyId != "colonyA")
            && f.State.Players.IsDeleted("userA", "colonyA"), "late registration resurrected the deleted colony");
    }

    private static void VerifyNestedMutation(Fixture f)
    {
        f.Store.BeforeRemove = f.Recover;
        Fails(() => f.Delete(admin: true));
        Require(f.State.ColonyDeletions.HasPending && f.Files.GetLatest("userA", "colonyA") is not null,
            "nested mutation was allowed during deletion");
        f.Store.BeforeRemove = null;
        f.Recover();
        Require(!f.State.ColonyDeletions.HasPending && f.Files.GetLatest("userA", "colonyA") is null,
            "nested mutation rejection poisoned future recovery");
    }

    private static void VerifyJournalValidation(Fixture f)
    {
        f.Store.Fault = "before";
        Fails(() => f.Delete(admin: false));
        string path = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        byte[] original = File.ReadAllBytes(path);
        foreach (string field in new[] { "Version", "UserId", "NotifyUsers" })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            if (field == "Version") json[field] = 2;
            else if (field == "UserId") json[field] = "wrong-owner";
            else json[field] = new System.Text.Json.Nodes.JsonArray();
            File.WriteAllText(path, json.ToJsonString());
            foreach (Action validate in new Action[] { () => _ = new ColonyDeletionService(f.Journal),
                () => new ServerPersistenceMigrationService(Path.GetDirectoryName(f.Journal)!).Assess() })
            {
                bool rejected = false;
                try { validate(); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "invalid journal was accepted: " + field);
            }
        }
        File.WriteAllBytes(path, original);
        f.Open();
        f.Recover();
        Require(!f.State.ColonyDeletions.HasPending, "valid restored journal could not be recovered");
    }

    private static void VerifyCompleted(Fixture f, ColonyDeletionIntent intent)
    {
        Require(!f.State.ColonyDeletions.HasPending && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "completed deletion retained work");
        Require(f.Files.GetLatest("userA", "colonyA") is null
            && f.State.WorldConfiguration.Current!.PlayerColonySites.Count == 2
            && f.State.WorldConfiguration.Current.PlayerColonySites.All(site => site.ColonyId != "colonyA"), "deletion left snapshot or world sites");
        Require(f.State.BankLoans.FindByIdempotencyKey("loan") is null
            && f.State.DiplomacyRelations.GetRelationKind("userA", "colonyA", "buyer", "buyer-colony") == DiplomacyRelationRegistry.RelationNeutral
            && f.State.RaidProtectionActivations.FindActivatedAt("raid", "userA", "colonyA") is null
            && f.State.MercenaryContracts.Find("contract")?.Status == MercenaryContractRegistry.StatusCompleted, "deletion left business records");
        var tombstone = f.State.Players.ListDeleted().Single(item => item.UserId == "userA" && item.ColonyId == "colonyA");
        Require(tombstone.DeletedAtUtc == intent.DeletedAtUtc && tombstone.LastSnapshotId == intent.Previous!.Identity.SnapshotId
            && f.State.Ledger.ListAll().Count == 3 && f.State.Ledger.Find("order")!.Status == ServerEventStatus.Cancelled
            && f.State.Ledger.Find("memo")!.Status == ServerEventStatus.Cancelled
            && f.State.Ledger.Find(intent.Tombstone.EventId)!.CreatedAtUtc == intent.DeletedAtUtc, "deletion changed tombstone identity or closure graph");
    }

    private sealed class Fixture
    {
        private readonly string root;
        public string Database => Path.Combine(root, "server.sqlite");
        public string Journal => Path.Combine(root, "colony-deletions");
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public SaveSnapshotPackage Package;
        public ClashOfRimNetworkState State = null!;
        public FileColonySnapshotIndexStore Files = null!;
        public CrashStore Store = null!;
        private string token = null!;
        public Fixture(string root, SaveSnapshotPackage package)
        {
            this.root = root; Package = package;
            ServerDatabaseMigrator.Migrate(Database);
            Open();
            Files.StoreLatest(Package, Package.Index, Now);
            ColonyDeletionStageTests.SeedWorld(State.WorldConfiguration, "userA", "colonyA");
            State.Players.RecordLatestSnapshotReference("userA", "colonyA", Package.Envelope.Identity.SnapshotId, Now);
            State.Players.RecordLatestSnapshotReference("buyer", "buyer-colony", "buyer-snapshot", Now);
            State.BankLoans.Create("loan", "userA", "colonyA", Package.Envelope.Identity.SnapshotId!, 100, 10, 1, 0, Now);
            State.DiplomacyRelations.SetRelationKind("userA", "colonyA", "buyer", "buyer-colony", DiplomacyRelationRegistry.RelationAlly, null, Now);
            State.RaidProtectionActivations.ActivateIfMissing("raid", "userA", "colonyA", Now);
            State.MercenaryContracts.Create("mercenary", "userA", "colonyA", Package.Envelope.Identity.SnapshotId!, "Shooting", 10, 1, 100, 10, 20, 0, Now, "contract");
            var order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new EventParty("userA", "colonyA"), new EventParty("server"), "order", false,
                new TradeEventPayload("order", TradeStage.MarketOrder, [], [], 0), Now) with { EventId = "order", Status = ServerEventStatus.Recorded };
            State.Ledger.Append(order);
            State.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, new EventParty("buyer", "buyer-colony"), new EventParty("buyer", "buyer-colony"), "memo", false,
                new TradeEventPayload("order", TradeStage.AcceptedMemo, [], [], 0), Now) with { EventId = "memo", Status = ServerEventStatus.Recorded });
        }
        public void Open()
        {
            Files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")); Store = new CrashStore(Files);
            State = new ClashOfRimNetworkState(snapshotStore: Store, ledger: new SqliteAuthoritativeEventLedger(Database),
                colonyDeletions: new ColonyDeletionService(Journal), playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null),
                bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.BankLoans), null),
                diplomacyRelations: new DiplomacyRelationRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.DiplomacyRelations), null),
                mercenaryContracts: new MercenaryContractRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.MercenaryContracts), null),
                raidProtectionActivations: new RaidProtectionActivationRegistry(new SqliteDomainKeyedJsonRecordStore(Database, SqliteDomainRegistrySchema.RaidProtectionActivations), null),
                worldConfigurationRegistry: new WorldConfigurationRegistry(new SqliteKeyedJsonRecordStore(Database, "world-configuration"), null, new SqliteJsonPersistenceSlot(Database, "world-configuration"), null));
            State.LoginSessions.TryCreate("userA", "colonyA", Now, out string session);
            token = State.AuthTokens.Issue("userA", "userA", "colonyA", session, Now);
        }
        public void Delete(bool admin)
        {
            if (admin)
            {
                var result = (ProtocolResponse)Invoke("DeletePlayerSave", State, "userA", "colonyA", Now, 0)!;
                Require(result.Accepted, "admin deletion rejected");
            }
            else
            {
                var result = (IValueHttpResult)Invoke("AbandonPlayerColony",
                    new AbandonPlayerColonyRequest(ProtocolApiVersion.Current, "userA", "colonyA", Package.Envelope.Identity.SnapshotId, "delete", token), State, NullLoggerFactory.Instance)!;
                Require(((AbandonPlayerColonyResponse)result.Value!).Result.Accepted, "player abandonment rejected");
            }
        }
        public void Recover() => Invoke("RecoverSnapshotCommits", State);
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + Database); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
    private sealed class CrashStore(IColonySnapshotPackageStore inner) : IColonySnapshotPackageStore
    {
        public string? Fault;
        public Action? BeforeRemove;
        public bool RemoveLatest(string user, string colony)
        {
            BeforeRemove?.Invoke();
            if (Fault == "before") throw new IOException("before delete");
            bool removed = inner.RemoveLatest(user, colony);
            if (Fault == "after") throw new IOException("after delete");
            return removed;
        }
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at) => inner.StoreLatest(package, index, at);
        public void StoreLatest(LatestSnapshotRecord snapshot) => inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string user, string colony) => inner.GetLatest(user, colony);
        public SaveSnapshotPackage? GetLatestPackage(string user, string colony) => inner.GetLatestPackage(user, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
    }
    private sealed class DeferredFileLock : IDisposable
    {
        private FileStream? stream;
        public void Lock(string path) => stream ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public void Dispose() => stream?.Dispose();
    }
    private static object? Invoke(string name, params object?[] args) => typeof(ClashOfRimNetworkServer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    private static void Fails(Action action)
    {
        try { action(); }
        catch (TargetInvocationException ex)
        {
            Exception failure = ex;
            while (failure is TargetInvocationException { InnerException: { } inner }) failure = inner;
            if (failure is IOException or SqliteException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) return;
            throw;
        }
        throw new Exception("Expected deletion failure.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void WithFixture(Func<string, SaveSnapshotPackage> fixture, Action<Fixture> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-deletion-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(new Fixture(root, fixture("deletion-snapshot"))); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
