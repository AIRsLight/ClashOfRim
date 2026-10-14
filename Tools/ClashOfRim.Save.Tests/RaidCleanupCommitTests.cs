using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class RaidCleanupCommitTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        foreach (bool timeout in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "before", "after", "player", "loss", "support", "status", "signal", "advanced", "newer", "removed", "token", "source", "support-source" })
            WithFixture(original, timeout, f => Recovery(f, fault, reopen));
        foreach (bool timeout in new[] { false, true })
        {
            WithFixture(original, timeout, f => Guard(f, "owner"));
            WithFixture(original, timeout, f => Guard(f, "raid"));
        }
    }

    private static void Recovery(Fixture f, string fault, bool reopen)
    {
        bool conflict = fault is "newer" or "removed" or "token" or "source" or "support-source";
        f.Store.Fault = fault == "after" ? "after" : fault == "before" || conflict ? "before" : null;
        if (fault == "player") f.Sql("create trigger fail_write before insert on server_players begin select raise(abort, 'player'); end;");
        if (fault is "loss" or "support") f.Sql("create trigger fail_write before insert on events when NEW.idempotency_key like '"
            + (fault == "loss" ? "raid-attacker-loss:%" : "support-loss:%") + "' begin select raise(abort, 'event'); end;");
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
            Fails(() => ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State), "Conflicting cleanup was recovered automatically.");
            Require(File.Exists(path) && f.Files.GetLatest("attacker", "colony-a")?.Envelope == latest, "Conflict lost its journal or overwritten snapshot.");
            return;
        }
        ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
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

    private static object? Invoke(string name, params object[] args)
    {
        try { return typeof(ClashOfRimNetworkServer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
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
    private sealed class Fixture
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
            Mutation = (RaidCleanupSnapshotMutation)Invoke("BuildRaidCleanupMutation", State, Raid, (RaidEventPayload)Raid.Payload,
                Original, Cleaned, result, Now, timeout ? "timeout" : "map-cleanup")!;
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
            if (timeout) Invoke("CleanupTimedOutRaidAttackerSnapshots", State, new[] { Raid }, Now);
            else Invoke("CleanupSettledRaidBattleSnapshotBeforeDownload", State, Store,
                Store.GetLatestPackage("attacker", "colony-a")!, "attacker", "colony-a", Now);
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
    private sealed class CrashStore(IColonySnapshotPackageStore inner) : IColonySnapshotPackageStore
    {
        public string? Fault;
        public int Writes;
        public SaveSnapshotPackage? ReadOverride;
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            Writes++;
            if (Fault == "before") throw new IOException("before file");
            inner.StoreLatest(package, index, at);
            if (Fault == "after") throw new IOException("after file");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => ReadOverride ?? inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
