using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static partial class RaidCleanupCommitTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        RunDownloadJournalRecovery(original);
        RunDownloadAuthorization(original);
        foreach (string change in new[] { "none", "newer", "token", "removed", "source", "owner", "new-raid", "support-closed", "delivery" })
            WithFixture(original, false, f => VerifyUnlockedPreparation(f, change));
        WithFixture(original, false, f =>
        {
            Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(f.InvokeCleanup))).GetAwaiter().GetResult();
            Require(f.Store.Writes == 1 && f.Store.PreparedWrites == 1 && f.State.Ledger.ListAll().Count == 4,
                "Concurrent download cleanup duplicated publication or losses.");
        });
        foreach (bool timeout in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "before", "after", "player", "loss", "support", "status", "signal", "advanced", "newer", "removed", "token", "source", "support-source" })
            WithFixture(original, timeout, f => Recovery(f, fault, reopen));
        foreach (bool timeout in new[] { false, true })
        {
            WithFixture(original, timeout, f => Guard(f, "owner"));
            WithFixture(original, timeout, f => Guard(f, "raid"));
            foreach (bool committed in new[] { false, true })
                WithFixture(original, timeout, f => LegacyKeyRecovery(f, committed));
        }
    }

    private static void VerifyUnlockedPreparation(Fixture f, string change)
    {
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            f.InvokeCleanup, () =>
            {
                Require(f.Store.Writes == 0 && f.State.Ledger.ListAll().Count == 2
                    && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "Cleanup published before preparation finished.");
                if (change is "newer" or "token")
                {
                    var envelope = change == "newer" ? f.Original.Envelope with { Identity = f.Original.Envelope.Identity with { SnapshotId = "newer" } }
                        : f.Original.Envelope with { NextLineageToken = "replacement-token" };
                    f.Files.StoreLatest(f.Original with { Envelope = envelope }, f.Original.Index, f.Now);
                }
                if (change == "removed") f.Files.RemoveLatest("attacker", "colony-a");
                if (change == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Failed);
                if (change == "owner") f.Sql("update events set actor_user_id = 'other' where event_id = 'raid-timeout-001';");
                if (change == "new-raid") f.State.Ledger.Append(f.Raid with
                    { EventId = "new-raid", IdempotencyKey = "new-raid", Status = ServerEventStatus.Recorded,
                        Payload = ((RaidEventPayload)f.Raid.Payload) with { FinishedAtUtc = null, AttackForce = new RaidAttackForceRecord("new-attack", [], []) } });
                if (change == "support-closed") f.State.Ledger.ChangeStatus(f.Support.EventId, ServerEventStatus.Cancelled);
                if (change == "delivery") f.State.Ledger.MarkDelivered(f.Support.EventId, "delivered-during-preparation", f.Now);
            });
        bool accepted = change is "none" or "support-closed" or "delivery";
        Require(f.Store.Preparations == 1 && f.Store.Writes == (accepted ? 1 : 0)
            && f.Store.PreparedWrites == f.Store.Writes && !Directory.EnumerateFiles(f.Journal, "*.json").Any(),
            "Cleanup lost prepared bytes or committed stale preparation: " + change);
        if (accepted)
        {
            Require(!f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent")
                && f.State.Ledger.ListAll().Count == (change == "support-closed" ? 3 : 4)
                && f.State.Ledger.Find(f.Support.EventId)!.Status == (change == "support-closed" ? ServerEventStatus.Cancelled : ServerEventStatus.Failed),
                "Cleanup did not rebuild its losses from current support state.");
            if (change == "delivery") Require(f.State.Ledger.Find(f.Support.EventId)!.DeliveredToSnapshotId == "delivered-during-preparation",
                "Cleanup lost a concurrent support delivery.");
        }
        else Require(f.State.Ledger.ListAll().Count == (change == "new-raid" ? 3 : 2)
            && f.Files.GetLatest("attacker", "colony-a")?.Identity.SnapshotId == (change == "removed" ? null : change == "newer" ? "newer" : f.Original.Envelope.Identity.SnapshotId)
            && (change != "token" || f.Files.GetLatest("attacker", "colony-a")!.Envelope.NextLineageToken == "replacement-token"),
            "Rejected cleanup altered the competing snapshot or ledger.");
    }

    private static void LegacyKeyRecovery(Fixture f, bool committed)
    {
        var generated = f.Mutation.Batch.Events.Single(item => item.Type == ServerEventType.SupportPawn);
        string key = $"support-loss:{f.Support.EventId}:RaidSettlementLost";
        var legacy = generated with { IdempotencyKey = key, EventId = AuthoritativeEventFactory.BuildLegacyEventId(generated.Type, key) };
        var mutation = f.Mutation with { Batch = f.Mutation.Batch with
            { Events = f.Mutation.Batch.Events.Select(item => item.EventId == generated.EventId ? legacy : item).ToArray() } };
        if (!committed) f.Store.Fault = "before";
        Fails(() => f.State.SnapshotCommits.CommitRaidCleanup(f.Store, f.State.BankLoans, f.State.Ledger, f.State.Players,
            f.Cleaned, mutation, f.Now, _ => throw new IOException("before wake")), "Legacy journal interruption not reached.");
        if (committed)
        {
            f.State.Ledger.MarkDelivered(legacy.EventId, "base", f.Now);
            legacy = f.State.Ledger.MarkApplied(legacy.EventId, "consumed", f.Now);
        }
        f.Open();
        f.State.SnapshotMutations.Recover();
        f.InvokeCleanup();
        var actual = f.State.Ledger.Find(legacy.EventId);
        Require(actual is not null && JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(legacy)
            && f.State.Ledger.Find(generated.EventId) is null && f.State.Ledger.ListAll().Count == 4
            && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "Legacy cleanup recovery renamed or duplicated support loss.");
    }

    private static void Recovery(Fixture f, string fault, bool reopen)
    {
        bool conflict = fault is "newer" or "removed" or "token" or "source" or "support-source";
        f.Store.Fault = fault == "after" ? "after" : fault == "before" || conflict ? "before" : null;
        if (fault == "player") f.Sql("create trigger fail_write before insert on server_players begin select raise(abort, 'player'); end;");
        if (fault is "loss" or "support") f.Sql("create trigger fail_write before insert on events when NEW.idempotency_key like '"
            + (fault == "loss" ? "raid-attacker-loss:%" : "supportlossv2:%") + "' begin select raise(abort, 'event'); end;");
        if (fault == "status") f.Sql("create trigger fail_write before update on events begin select raise(abort, 'status'); end;");
        if (fault is "signal" or "advanced")
            Fails(() => f.State.SnapshotCommits.CommitRaidCleanup(f.Store, f.State.BankLoans, f.State.Ledger, f.State.Players,
                f.Cleaned, f.Mutation, f.Now, _ => throw new IOException("signal")), "Expected signal failure.");
        else Fails(() => f.InvokeCleanup(), "Cleanup swallowed a commit failure and returned a successful stale download.");
        string path = Directory.EnumerateFiles(f.Journal, "*.json").Single();
        var intent = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(path))!;
        Require(intent.Version == 9 && intent.RaidCleanup?.Batch.Events.Count == 2, "Cleanup did not persist the full loss batch.");
        if (fault is "player" or "loss" or "support" or "status")
        {
            Require(f.State.Ledger.ListAll().Count == 2 && f.State.Ledger.Find("support-in-raid")!.Status == f.Support.Status,
                "Failed cleanup batch exposed partial notifications or support failure.");
            f.Sql("drop trigger fail_write;");
        }
        if (fault is "newer" or "token")
        {
            var envelope = f.Original.Envelope;
            envelope = fault == "newer" ? envelope with { Identity = envelope.Identity with { SnapshotId = "newer" } }
                : envelope with { NextLineageToken = "new-token" };
            f.Files.StoreLatest(f.Original with { Envelope = envelope }, f.Original.Index, f.Now);
        }
        if (fault == "removed") f.Files.RemoveLatest("attacker", "colony-a");
        if (fault == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
        if (fault == "support-source") f.State.Ledger.ChangeStatus(f.Support.EventId, ServerEventStatus.Cancelled);
        AuthoritativeEvent? advanced = null;
        if (fault == "advanced")
        {
            string id = intent.RaidCleanup!.Batch.Events.First(item => item.Type == ServerEventType.SupportPawn).EventId;
            f.State.Ledger.MarkDelivered(id, "later", f.Now.AddHours(1));
            advanced = f.State.Ledger.MarkApplied(id, "later", f.Now.AddHours(2));
        }
        f.Store.Fault = null;
        if (reopen) f.Open();
        var latest = f.Files.GetLatest("attacker", "colony-a")?.Envelope;
        if (conflict)
        {
            Fails(() => f.State.SnapshotMutations.Recover(), "Conflicting cleanup was recovered automatically.");
            Require(File.Exists(path) && f.Files.GetLatest("attacker", "colony-a")?.Envelope == latest, "Conflict lost its journal or overwritten snapshot.");
            return;
        }
        f.State.SnapshotMutations.Recover();
        f.InvokeCleanup();
        Require(!Directory.EnumerateFiles(f.Journal, "*.json").Any() && f.State.Ledger.ListAll().Count == 4
            && f.State.Ledger.Find(f.Support.EventId)!.Status == ServerEventStatus.Failed
            && f.Files.GetLatestPackage("attacker", "colony-a")!.Payload.SequenceEqual(intent.Package.Payload)
            && f.Files.GetLatest("attacker", "colony-a")!.Envelope == intent.Package.Envelope
            && f.State.Players.FindByUserId("attacker")!.CurrentSnapshotId == intent.Package.Envelope.Identity.SnapshotId,
            "Cleanup recovery lost or duplicated snapshot/reference/notifications.");
        if (advanced is not null) Require(JsonSerializer.Serialize(f.State.Ledger.Find(advanced.EventId)) == JsonSerializer.Serialize(advanced),
            "Cleanup recovery reset later support notification application.");
        f.Open();
        Require(f.State.Ledger.ListAll().Count == 4 && f.State.Players.FindByUserId("attacker")!.CurrentSnapshotId == intent.Package.Envelope.Identity.SnapshotId,
            "Recovered cleanup did not persist.");
    }

    private static void Guard(Fixture f, string change)
    {
        if (change == "owner") f.Sql("update events set actor_user_id = 'other' where event_id = 'raid-timeout-001';");
        else
        {
            var package = f.Original with { Index = f.Original.Index with { WorldObjects = f.Original.Index.WorldObjects
                .Select(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent" ? item with { ClashOfRimRelatedEventId = "new-raid" } : item).ToArray() } };
            f.Store.ReadOverride = package;
        }
        f.Raid = f.State.Ledger.Find(f.Raid.EventId)!;
        f.InvokeCleanup();
        Require(f.Store.Writes == 0 && f.State.Ledger.ListAll().Count == 2 && !Directory.EnumerateFiles(f.Journal, "*.json").Any(),
            "Cleanup targeted a different owner or raid battle.");
    }


    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Fails(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or SqliteException) { return; }
        throw new Exception(message);
    }
    private static void WithFixture(SaveSnapshotPackage original, bool timeout, Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-raid-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(new Fixture(root, original, timeout)); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed partial class Fixture
    {
        private readonly string root;
        private readonly bool timeout;
        public string Database => Path.Combine(root, "server.sqlite");
        public string Journal => Path.Combine(root, "intents");
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddHours(3);
        public SaveSnapshotPackage Original, Cleaned;
        public RaidCleanupSnapshotMutation Mutation;
        public AuthoritativeEvent Raid, Support;
        public ClashOfRimNetworkState State = null!;
        public FileColonySnapshotIndexStore Files = null!;
        public CrashStore Store = null!;
        public Fixture(string root, SaveSnapshotPackage original, bool timeout)
        {
            this.root = root;
            this.timeout = timeout;
            Original = original;
            ServerDatabaseMigrator.Migrate(Database);
            Raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new EventParty("attacker", "colony-a"),
                new EventParty("defender", "defender-colony"), "cleanup-raid", false,
                new RaidEventPayload("defender-before", null, Now.AddHours(-2), Now, null), Now.AddHours(-2)) with
                { EventId = "raid-timeout-001", Status = timeout ? ServerEventStatus.Failed : ServerEventStatus.AppliedToSnapshot };
            Support = AuthoritativeEventFactory.Create(ServerEventType.SupportPawn, new EventParty("ally", "ally-colony"), Raid.Actor,
                "support-in-raid", false, new SupportPawnEventPayload("owner:ally/colony:ally-colony/snapshot:support/map:caravan/thing:raid-support",
                    "support", "Support", true, null), Now.AddHours(-1)) with { EventId = "support-in-raid" };
            Open();
            State.Ledger.Append(Raid);
            State.Ledger.Append(Support);
            Files.StoreLatest(Original, Original.Index, Now);
            State.Players.RecordLatestSnapshotReference("attacker", "colony-a", Original.Envelope.Identity.SnapshotId, Now);
            Require(RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(Original, "cleaned", Now, out Cleaned,
                out RaidAttackerSnapshotCleanupResult result), "Cleanup fixture was not removable.");
            Mutation = State.RaidBattleCleanup.BuildMutation(Raid, (RaidEventPayload)Raid.Payload,
                Original, Cleaned, result, Now, timeout ? "timeout" : "map-cleanup");
        }
        public void Open()
        {
            Files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
            Store = new CrashStore(Files);
            State = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal));
        }
        public void InvokeCleanup()
        {
            if (timeout) State.RaidBattleCleanup.CleanupTimeouts(new[] { Raid }, Now);
            else State.RaidBattleCleanup.BeforeDownload(Store.GetLatestPackage("attacker", "colony-a")!, "attacker", "colony-a", Now);
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
        public Action? BeforePrepare, AfterRead;
        public bool FailPreparation;
        public Dictionary<string, int> WritesByOwner = new();
        public PreparedSnapshotPayload PreparePayload(byte[] bytes)
        {
            Interlocked.Increment(ref Preparations);
            BeforePrepare?.Invoke();
            if (FailPreparation) throw new IOException("recovery preparation failed");
            return inner.PreparePayload(bytes);
        }
        public void StorePrepared(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at, PreparedSnapshotPayload prepared)
        {
            Writes++; PreparedWrites++;
            if (Fault == "before") throw new IOException("before file");
            inner.StorePrepared(package, index, at, prepared);
            WritesByOwner[package.Envelope.Identity.OwnerId!] = WritesByOwner.GetValueOrDefault(package.Envelope.Identity.OwnerId!) + 1;
            if (Fault == "after") throw new IOException("after file");
        }
        public SaveSnapshotPackage? ReadOverride;
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            Writes++;
            if (Fault == "before") throw new IOException("before file");
            inner.StoreLatest(package, index, at);
            WritesByOwner[package.Envelope.Identity.OwnerId!] = WritesByOwner.GetValueOrDefault(package.Envelope.Identity.OwnerId!) + 1;
            if (Fault == "after") throw new IOException("after file");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony)
        {
            var package = ReadOverride ?? inner.GetLatestPackage(owner, colony);
            AfterRead?.Invoke();
            return package;
        }
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
