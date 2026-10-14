using System.Reflection;
using System.Runtime.ExceptionServices;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class RaidTimeoutRecoveryTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        foreach (bool offline in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "notification", "mark", "before", "after", "loss", "ack", "schedule", "settled" })
        {
            if (!offline && fault is "schedule" or "settled") continue;
            WithFixture(original, f => Recovery(f, offline, reopen, fault));
        }
        foreach (string kind in new[] { "memory", "file", "sqlite" }) WithFixture(original, f => VerifyBatch(f, kind));
        WithFixture(original, VerifyBackgroundRecovery);
    }

    private static void Recovery(Fixture f, bool offline, bool reopen, string fault)
    {
        if (!offline) Require(f.State.OnlinePresence.TryConnectExclusive("attacker", out _), "Could not seed online attacker.");
        if (fault == "notification") f.Sql("create trigger fail_write before insert on events when NEW.target_user_id = 'defender' and NEW.type = 'ServerNotification' begin select raise(abort, 'notification'); end;");
        if (fault == "mark") f.Sql("create trigger fail_write before update on events begin select raise(abort, 'timeout marker'); end;");
        if (fault == "schedule") f.Sql("create trigger fail_write before insert on server_snapshot_post_upload_jobs begin select raise(abort, 'job'); end;");
        if (fault == "loss") f.Sql("create trigger fail_write before insert on events when NEW.idempotency_key like 'raid-attacker-loss:%' begin select raise(abort, 'loss'); end;");
        if (fault is "ack" or "settled") f.Sql("create trigger fail_write before update on events when json_extract(NEW.payload_json, '$.TimeoutWork.Completed') = 1 begin select raise(abort, 'ack'); end;");
        f.Store.Fault = fault is "before" or "after" ? fault : null;
        Fails(f.Reconcile, "Expected timeout processing interruption.");
        var source = f.State.Ledger.Find(f.Raid.EventId)!;
        if (fault is "notification" or "mark")
            Require(source.Status == f.Raid.Status && ((RaidEventPayload)source.Payload).TimeoutWork is null
                && f.State.Ledger.ListAll().Count == 1, "Timeout notification failure leaked status, marker or a partial pair.");
        else
            Require(((RaidEventPayload)source.Payload).TimeoutWork is { Completed: false } work
                && work.AttackerWasOffline == offline && work.TimedOutAtUtc == f.Now
                && f.State.Ledger.ListAll().Count(item => item.Type == ServerEventType.ServerNotification) == 2,
                "Interrupted downstream work lost its durable timeout classification.");
        if (fault is "notification" or "mark" or "schedule" or "loss" or "ack" or "settled") f.Sql("drop trigger fail_write;");
        f.Store.Fault = null;
        if (fault == "settled")
        {
            Require(SnapshotPostUploadJobExecutor.ProcessReady(f.State, [new RaidSettlementPostUploadProcessor()], f.Now.AddMinutes(1)) == 1,
                "Could not complete settlement before timeout acknowledgement retry.");
            Require(f.State.Ledger.Find(f.Raid.EventId)!.Status == ServerEventStatus.AppliedToSnapshot, "Settlement did not advance source.");
        }
        if (reopen) f.Open();
        // Reverse current presence to prove retry uses the persisted decision, not the new login state.
        bool timeoutCommitted = fault is not ("notification" or "mark");
        if (offline && timeoutCommitted) f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        else if (!offline && reopen && !timeoutCommitted) f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        if (!timeoutCommitted) f.Reconcile();
        else ClashOfRimNetworkServer.RecoverPendingRaidTimeoutWork(f.State, f.Now);
        f.Reconcile();
        source = f.State.Ledger.Find(f.Raid.EventId)!;
        Require(((RaidEventPayload)source.Payload).TimeoutWork is { Completed: true } completed
            && completed.AttackerWasOffline == offline
            && f.State.Ledger.ListAll().Count(item => item.Type == ServerEventType.ServerNotification) == 2
            && f.State.Ledger.ListAll().Count(item => item.Payload is RaidEventPayload { AttackerLoss: not null }) == 1
            && !f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent")
            && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), $"Timeout retry failed: offline={offline}, reopen={reopen}, fault={fault}, "
                + System.Text.Json.JsonSerializer.Serialize(source));
        Require(f.State.SnapshotPostUploadJobs.ListAll().Count == (offline && fault != "settled" ? 1 : 0),
            "Timeout retry lost or duplicated the durable settlement job.");
        f.Open();
        Require(RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0, "Timeout completion did not persist.");
    }

    private static void VerifyBackgroundRecovery(Fixture f)
    {
        RaidTimeoutProcessor.ProcessExpiredRaids(f.State.Ledger, f.State.Ledger.ListAll(), f.Now, isAttackerOnline: _ => true);
        f.Open();
        using var service = new SnapshotPostUploadBackgroundService(f.State, new SnapshotPostUploadProcessorSource(() => []));
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count != 0 && DateTime.UtcNow < deadline)
                Task.Delay(20).GetAwaiter().GetResult();
            Require(RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0
                && f.State.Ledger.ListAll().Count == 4
                && !f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent"),
                "Background service did not recover persisted timeout work without a new request.");
        }
        finally { service.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private static void VerifyBatch(Fixture f, string kind)
    {
        string path = Path.Combine(f.Root, "ledger.json");
        IAuthoritativeEventLedger ledger = kind switch
        {
            "memory" => new InMemoryAuthoritativeEventLedger(),
            "file" => new FileAuthoritativeEventLedger(path),
            _ => new SqliteAuthoritativeEventLedger(f.Database)
        };
        ledger.Append(f.Raid);
        if (kind == "file" && OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Fails(() => RaidTimeoutProcessor.ProcessExpiredRaids(ledger, ledger.ListAll(), f.Now), "Expected file publication failure.");
            Require(ledger.ListAll().Count == 1 && ledger.Find(f.Raid.EventId)!.Status == f.Raid.Status, "Failed file batch leaked timeout state.");
        }
        var result = RaidTimeoutProcessor.ProcessExpiredRaids(ledger, ledger.ListAll(), f.Now);
        Require(result.NotificationCount == 2 && RaidTimeoutProcessor.PendingWork(ledger).Count == 1, "Timeout did not atomically publish work and notices.");
        var current = ledger.Find(f.Raid.EventId)!;
        var wrongPayload = ((RaidEventPayload)current.Payload) with { DefenderSnapshotId = "wrong" };
        Fails(() => ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(current.EventId, current.Status, current.Status,
            wrongPayload, wrongPayload with { TimeoutWork = null })])), "Stale payload update was accepted.");
        RaidTimeoutProcessor.CompleteWork(ledger, f.Raid.EventId);
        RaidTimeoutProcessor.CompleteWork(ledger, f.Raid.EventId);
        if (kind == "file") ledger = new FileAuthoritativeEventLedger(path);
        Require(ledger.ListAll().Count == 3 && RaidTimeoutProcessor.PendingWork(ledger).Count == 0, "Completion changed notification count or failed persistence.");
        ledger.Append(f.Raid with { EventId = "legacy-failed", IdempotencyKey = "legacy-failed", Status = ServerEventStatus.Failed });
        Require(RaidTimeoutProcessor.ProcessExpiredRaids(ledger, ledger.ListAll(), f.Now).NotificationCount == 0
            && RaidTimeoutProcessor.PendingWork(ledger).Count == 0, "Legacy failed raid was reclassified without a persisted timeout decision.");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Fails(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or SqliteException) { return; }
        throw new Exception(message);
    }
    private static void WithFixture(SaveSnapshotPackage original, Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(new Fixture(root, original)); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class Fixture
    {
        public string Root { get; }
        public string Database => Path.Combine(Root, "server.sqlite");
        public string Journal => Path.Combine(Root, "intents");
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddHours(3);
        public AuthoritativeEvent Raid;
        public FileColonySnapshotIndexStore Files = null!;
        public CrashStore Store = null!;
        public ClashOfRimNetworkState State = null!;
        public Fixture(string root, SaveSnapshotPackage original)
        {
            Root = root;
            ServerDatabaseMigrator.Migrate(Database);
            Raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new EventParty("attacker", "colony-a"),
                new EventParty("defender", "defender-colony"), "timeout", false,
                new RaidEventPayload("defender-before", null, DateTimeOffset.UnixEpoch, null, null), DateTimeOffset.UnixEpoch,
                new EventTargetContext("WorldObject_Defender", "0", 1, EventLandingMode.MapEdge)) with { EventId = "raid-timeout-001" };
            Open();
            State.Ledger.Append(Raid);
            Files.StoreLatest(original, original.Index, Now);
            string save = Path.Combine(root, "defender.rws");
            File.WriteAllText(save, "<savegame><meta><gameVersion>1.7-test</gameVersion></meta><game><maps><li><uniqueID>0</uniqueID><things /></li></maps></game></savegame>");
            var defender = SaveSnapshotPackageBuilder.FromFile(save, new SnapshotIdentity("defender", "defender-colony", "defender-before"), Now);
            Files.StoreLatest(defender, defender.Index, Now);
        }
        public void Open()
        {
            Files = new FileColonySnapshotIndexStore(Path.Combine(Root, "snapshots"));
            Store = new CrashStore(Files);
            State = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
                snapshotPostUploadJobs: new SnapshotPostUploadJobRegistry(new SqliteSnapshotPostUploadJobStore(Database)),
                snapshotPostUploadArtifacts: new FileSnapshotPostUploadArtifactStore(Path.Combine(Root, "artifacts")));
        }
        public void Reconcile()
        {
            try { typeof(ClashOfRimNetworkServer).GetMethod("ReconcileExpiredRaidEvents", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [State, Now]); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
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
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (Fault == "before") throw new IOException("before cleanup");
            inner.StoreLatest(package, index, at);
            if (Fault == "after") throw new IOException("after cleanup");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
