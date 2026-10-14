using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class RaidEvidenceRecoveryTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        foreach (bool reopen in new[] { false, true })
        {
            foreach (string fault in new[] { "before", "after", "ready", "player", "enqueue" })
                WithStorage(fixture, f => VerifyRecovery(f, fault, reopen));
            foreach (string conflict in new[] { "newer", "removed", "hash", "payload", "token", "evidence-hash", "evidence-token", "artifact", "missing-artifact", "binding", "legacy" })
                WithStorage(fixture, f => VerifyConflict(f, conflict, reopen));
            WithStorage(fixture, f => VerifyLegacyInstalled(f, reopen));
        }
        WithStorage(fixture, VerifyRetriesAndStaleScans);
        WithStorage(fixture, VerifyPreparationRace);
    }

    private static void VerifyRecovery(Fixture f, string fault, bool reopen)
    {
        f.Faults.Fault = fault;
        if (fault is "ready" or "enqueue") f.Sql("create trigger fail_job before insert on server_snapshot_post_upload_jobs "
            + (fault == "ready" ? "when NEW.job_state = 1 " : "") + "begin select raise(abort, 'job failure'); end;");
        if (fault == "player") f.Sql("create trigger fail_player before insert on server_players begin select raise(abort, 'player failure'); end;");
        Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, f.Request), "Fault did not interrupt scheduling.");
        var prepared = f.State.SnapshotPostUploadJobs.ListAll().SingleOrDefault();
        Require(fault == "enqueue" ? prepared is null : prepared?.State == SnapshotPostUploadJobState.Prepared,
            "Interrupted publication discarded or prematurely readied its intent.");
        if (prepared is not null) Require(f.State.SnapshotPostUploadArtifacts.Exists(Payload(prepared).EvidenceArtifactId), "Lost recovery evidence.");
        Require(f.Files.GetLatest("userA", "colonyA")!.Identity.SnapshotId == (fault is "before" or "enqueue" ? "before" : "evidence"),
            "Unexpected snapshot publication boundary.");
        if (fault is "ready" or "enqueue") f.Sql("drop trigger fail_job;");
        if (fault == "player") f.Sql("drop trigger fail_player;");
        f.Faults.Fault = null;
        if (reopen) f.Open();
        if (prepared is not null)
        {
            var latest = f.Files.GetLatest("userA", "colonyA")!.Envelope;
            Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, f.Request with { ClientApplicationResult = "changed" }),
                "Changed retry recovered a Prepared job.");
            Require(f.State.SnapshotPostUploadJobs.Find(prepared.JobId)!.State == SnapshotPostUploadJobState.Prepared
                && f.Files.GetLatest("userA", "colonyA")!.Envelope == latest, "Rejected retry changed publication state.");
        }
        var ready = fault == "enqueue" ? RaidSettlementDeferredScheduler.Schedule(f.State, f.Request)
            : RaidSettlementDeferredScheduler.RecoverPrepared(f.State, prepared!, f.Now);
        RaidSettlementDeferredScheduler.RecoverPrepared(f.State, prepared ?? ready, f.Now);
        Require(ready.State == SnapshotPostUploadJobState.Ready
            && f.State.SnapshotPostUploadJobs.ListAll().Count == 1
            && f.Files.GetLatestPackage("userA", "colonyA")!.Payload.SequenceEqual(f.Evidence.Payload)
            && f.Files.GetLatest("userA", "colonyA")!.Envelope.NextLineageToken == "evidence-token"
            && f.State.Players.FindByUserId("userA")!.CurrentSnapshotId == "evidence",
            "Recovery did not publish evidence and repair the player reference exactly once.");
        f.Open();
        Require(f.State.SnapshotPostUploadJobs.ListAll().Single().State == SnapshotPostUploadJobState.Ready
            && f.State.Players.FindByUserId("userA")!.CurrentSnapshotId == "evidence", "Ready state was not durable.");
    }

    private static void VerifyConflict(Fixture f, string conflict, bool reopen)
    {
        f.Faults.Fault = conflict.StartsWith("evidence-") ? "after" : "before";
        Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, f.Request), "Expected a prepared intent.");
        var prepared = f.State.SnapshotPostUploadJobs.ListAll().Single();
        var payload = Payload(prepared);
        if (conflict == "removed") f.Files.RemoveLatest("userA", "colonyA");
        if (conflict is "newer" or "hash" or "payload" or "token" or "evidence-hash" or "evidence-token")
        {
            var source = conflict.StartsWith("evidence-") ? f.Evidence : f.Before;
            var envelope = source.Envelope;
            envelope = conflict switch
            {
                "newer" => envelope with { Identity = envelope.Identity with { SnapshotId = "newer" } },
                "hash" or "evidence-hash" => envelope with { OriginalSha256 = "changed" },
                "payload" => envelope with { PayloadSha256 = "changed" },
                _ => envelope with { NextLineageToken = "changed" }
            };
            f.Files.StoreLatest(source with { Envelope = envelope }, source.Index, f.Now);
        }
        if (conflict is "artifact" or "missing-artifact")
        {
            f.State.SnapshotPostUploadArtifacts.Delete(payload.EvidenceArtifactId);
            if (conflict == "artifact") f.State.SnapshotPostUploadArtifacts.Store(payload.EvidenceArtifactId,
                f.Evidence with { Envelope = f.Evidence.Envelope with { NextLineageToken = "changed" } });
        }
        if (conflict is "binding" or "legacy")
        {
            payload = conflict == "legacy" ? payload with { Publication = null } : payload with { AttackerUserId = "other" };
            new SqliteSnapshotPostUploadJobStore(f.Database).Upsert(prepared with { PayloadJson = payload.Serialize() });
            // Reload the registry even in the same-process case so it observes the persisted conflict.
            f.Open();
        }
        f.Faults.Fault = null;
        if (reopen) f.Open();
        var latest = f.Files.GetLatest("userA", "colonyA")?.Envelope;
        var player = f.State.Players.FindByUserId("userA");
        Fails(() => RaidSettlementDeferredScheduler.RecoverPrepared(f.State, prepared, f.Now), "Conflicting recovery was accepted.");
        Require(f.Files.GetLatest("userA", "colonyA")?.Envelope == latest
            && f.State.Players.FindByUserId("userA") == player
            && f.State.SnapshotPostUploadJobs.ListPrepared().Count == 1
            && f.State.SnapshotPostUploadArtifacts.Exists(payload.EvidenceArtifactId) == (conflict != "missing-artifact"),
            "Conflict overwrote the destination/reference or discarded recovery data.");
    }

    private static void VerifyLegacyInstalled(Fixture f, bool reopen)
    {
        f.Faults.Fault = "after";
        Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, f.Request), "Expected interruption after installation.");
        var job = f.State.SnapshotPostUploadJobs.ListPrepared().Single();
        new SqliteSnapshotPostUploadJobStore(f.Database).Upsert(job with { PayloadJson = (Payload(job) with { Publication = null }).Serialize() });
        f.Open();
        if (reopen) f.Open();
        var ready = RaidSettlementDeferredScheduler.RecoverPrepared(f.State, job, f.Now);
        Require(ready.State == SnapshotPostUploadJobState.Ready && f.Faults.Writes == 0,
            "Legacy evidence already installed exactly should repair its reference without rewriting.");
    }

    private static void VerifyRetriesAndStaleScans(Fixture f)
    {
        var concurrent = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            RaidSettlementDeferredScheduler.Schedule(f.State, f.Request)))).GetAwaiter().GetResult();
        var ready = concurrent[0];
        Require(concurrent.All(job => job == ready) && f.Faults.Writes == 1, "Concurrent scheduling published more than once.");
        Require(RaidSettlementDeferredScheduler.Schedule(f.State, f.Request) == ready, "Identical retry changed the job.");
        foreach (var changed in new[]
        {
            f.Request with { Origin = RaidSettlementOrigin.OfflineTimeout },
            f.Request with { ClientApplicationResult = "changed" },
            f.Request with { EvidencePayload = [1, 2, 3] },
            f.Request with { Context = f.Request.Context with { UserId = "other" } },
            f.Request with { Context = f.Request.Context with { Snapshot = f.Request.Context.Snapshot with
                { Envelope = f.Evidence.Envelope with { OriginalSha256 = "changed" } } } }
        }) Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, changed), "Conflicting retry reused the original job.");

        var newer = f.Before with { Envelope = f.Before.Envelope with { Identity = f.Before.Envelope.Identity with { SnapshotId = "newer" } } };
        f.Files.StoreLatest(newer, newer.Index, f.Now);
        f.State.Players.RecordLatestSnapshotReference("userA", "colonyA", "newer", f.Now);
        var stale = ready with { State = SnapshotPostUploadJobState.Prepared };
        RaidSettlementDeferredScheduler.RecoverPrepared(f.State, stale, f.Now);
        f.State.SnapshotPostUploadJobs.MarkManualReview(ready.JobId, "review");
        RaidSettlementDeferredScheduler.RecoverPrepared(f.State, stale, f.Now);
        Require(f.State.SnapshotPostUploadJobs.Find(ready.JobId)!.State == SnapshotPostUploadJobState.ManualReview, "Stale scan reset manual review.");
        f.State.SnapshotPostUploadJobs.MarkCompleted(ready.JobId);
        f.State.SnapshotPostUploadArtifacts.Delete(Payload(ready).EvidenceArtifactId);
        RaidSettlementDeferredScheduler.RecoverPrepared(f.State, stale, f.Now);
        Require(f.Files.GetLatest("userA", "colonyA")!.Identity.SnapshotId == "newer"
            && f.State.Players.FindByUserId("userA")!.CurrentSnapshotId == "newer"
            && f.State.SnapshotPostUploadJobs.ListAll().Count == 0, "Stale scan restored an obsolete version or job.");
    }

    private static void VerifyPreparationRace(Fixture f)
    {
        f.Files.StoreLatest(f.Before with { Envelope = f.Before.Envelope with { NextLineageToken = "newer-token" } }, f.Before.Index, f.Now);
        Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, f.Request), "Stale prepared evidence overwrote a newer baseline.");
        f.Files.RemoveLatest("userA", "colonyA");
        Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, f.Request), "Stale preparation resurrected a deleted colony.");
        Require(f.State.SnapshotPostUploadJobs.ListAll().Count == 0, "Rejected preparation queued a job.");
    }

    private static RaidSettlementDeferredPayload Payload(SnapshotPostUploadJobRecord job) => RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
    private static void Fails(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or SqliteException) { return; }
        throw new Exception(message);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithStorage(Func<string, SaveSnapshotPackage> fixture, Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(new Fixture(root, fixture)); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class Fixture
    {
        private readonly string root;
        public string Database { get; }
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddHours(1);
        public SaveSnapshotPackage Before { get; }
        public SaveSnapshotPackage Evidence { get; }
        public FileColonySnapshotIndexStore Files = null!;
        public CrashStore Faults = null!;
        public ClashOfRimNetworkState State = null!;
        public RaidSettlementScheduleRequest Request = null!;
        private readonly AuthoritativeEvent raid;

        public Fixture(string root, Func<string, SaveSnapshotPackage> fixture)
        {
            this.root = root;
            Database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(Database);
            var baseline = fixture("before");
            Before = baseline with { Envelope = baseline.Envelope with { NextLineageToken = "before-token" } };
            var evidence = fixture("evidence");
            Evidence = evidence with { Envelope = evidence.Envelope with { PreviousSnapshotId = "before", LineageToken = "before-token", NextLineageToken = "evidence-token" } };
            raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new EventParty("userA", "colonyA"),
                new EventParty("userB", "colonyB"), "raid-evidence", false,
                new RaidEventPayload("defender", null, Now, null, null, new RaidAttackForceRecord("before", [], [])), Now);
            Open();
            State.Ledger.Append(raid);
            Files.StoreLatest(Before, Before.Index, Now);
            State.Players.RecordLatestSnapshotReference("userA", "colonyA", "before", Now);
            var defender = fixture("defender");
            defender = defender with { Envelope = defender.Envelope with { Identity = new SnapshotIdentity("userB", "colonyB", "defender") } };
            Files.StoreLatest(defender, defender.Index, Now);
        }

        public void Open()
        {
            Files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
            Faults = new CrashStore(Files);
            State = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Faults,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null),
                snapshotPostUploadJobs: new SnapshotPostUploadJobRegistry(new SqliteSnapshotPostUploadJobStore(Database)),
                snapshotPostUploadArtifacts: new FileSnapshotPostUploadArtifactStore(Path.Combine(root, "artifacts")));
            var context = new SnapshotPostUploadContext(State, SnapshotPostUploadKind.RaidSettlementEvidence,
                new LatestSnapshotRecord(Evidence.Envelope.Identity, Evidence.Envelope, Evidence.Index, Now),
                "userA", "colonyA", null, Now, SnapshotPostUploadExtraData.Empty, false);
            Request = new RaidSettlementScheduleRequest(raid.EventId, context, Evidence.Payload, RaidSettlementOrigin.OnlineEvidence, "Applied");
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
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            Writes++;
            if (Fault == "before") throw new IOException("before publication");
            inner.StoreLatest(package, index, at);
            if (Fault == "after") throw new IOException("after publication");
        }
        public void StoreLatest(LatestSnapshotRecord snapshot) => inner.StoreLatest(snapshot);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) => inner.GetLatestPackage(owner, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => inner.RemoveLatest(owner, colony);
    }
}
