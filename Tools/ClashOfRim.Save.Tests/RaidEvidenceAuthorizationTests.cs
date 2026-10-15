using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static partial class RaidEvidenceAuthorizationTests
{
    public static void Run(Func<string, string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        RunOnline(fixture);
        RunBindings(fixture);
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "cancel", "source-content" })
            WithFixture(fixture, f => VerifyPreparing(f, change));
        foreach (bool applied in new[] { false, true })
        foreach (string identity in new[] { "attacker", "defender", "delegated", "header", "metadata" })
            WithFixture(fixture, f => VerifyIdentity(f, applied, identity));
    }

    private static void VerifyPreparing(Fixture f, string change)
    {
        ConfirmEventApplicationResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Invoke(), () =>
            {
                Require(f.State.SnapshotPostUploadJobs.ListAll().Count == 0 && f.Store.Inner.GetLatest("userA", "colonyA") is null,
                    "Evidence published before preparation finished.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
                if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
                if (change == "revoke") f.State.AuthTokens.RevokeForUser("userA");
                if (change == "source-content")
                {
                    var source = f.State.Ledger.Find(f.Raid.EventId)!;
                    var payload = (RaidEventPayload)source.Payload;
                    f.State.Ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(source.EventId, source.Status, source.Status, payload,
                        payload with { StartedAtUtc = payload.StartedAtUtc!.Value.AddMinutes(1) })]));
                }
                if (change == "cancel") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
            });
        bool accepted = change == "none";
        Require(reply!.Result.Accepted == accepted, "Raid evidence accepted a revoked/cancelled preparation: " + change);
        Require(f.State.SnapshotPostUploadJobs.ListAll().Count == (accepted ? 1 : 0)
            && (f.Store.Inner.GetLatest("userA", "colonyA") is not null) == accepted
            && Directory.EnumerateFiles(f.Artifacts, "*.snapshot.gz").Count() == (accepted ? 1 : 0)
            && f.State.Ledger.ListAll().Count == 1, "Rejected evidence published an artifact, snapshot or job.");
        if (!accepted) Require(reply.AppliedSnapshotId is null && reply.NextLineageToken is null, "Rejected evidence disclosed snapshot credentials.");
    }

    private static void VerifyIdentity(Fixture f, bool applied, string identity)
    {
        if (applied)
        {
            f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.AppliedToSnapshot);
            f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with { NextLineageToken = "attacker-next" } }, f.Package.Index, DateTimeOffset.UtcNow);
        }
        string before = f.StateEvidence();
        var reply = f.Invoke(identity);
        if (applied && identity == "delegated")
            Require(reply.NextLineageToken != "defender-next", "Cross-party raid replay disclosed the defender lineage token.");
        bool accepted = identity == "attacker" && !applied;
        Require(reply.Result.Accepted == accepted, "Raid evidence did not bind its caller/metadata to the attacker: " + applied + "/" + identity);
        if (!accepted)
            Require(reply.AppliedSnapshotId is null && reply.NextLineageToken is null && f.StateEvidence() == before,
                "Rejected or wrong-party replay leaked snapshot credentials or changed persistent state.");

    }

    private static void WithFixture(Func<string, string, SaveSnapshotPackage> fixture, Action<Fixture> run, bool persistent = false, bool legacyArtifacts = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-raid-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new Fixture(root, fixture, persistent, legacyArtifacts);
            run(f);
            Require(f.ArtifactStore.ActiveHandles == 0, "Online request leaked artifact revision handles.");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed partial class Fixture
    {
        public string Artifacts { get; }
        public SaveSnapshotPackage Package { get; }
        public ObservedStore Store { get; private set; } = null!;
        public ObservedArtifacts ArtifactStore { get; private set; } = null!;
        public Action<AIRsLight.ClashOfRim.Network.Plugins.SnapshotPostUploadContext>? BeforeSchedule, AfterSchedule;
        public ClashOfRimNetworkState State { get; private set; } = null!;
        public AuthoritativeEvent Raid { get; }
        private string attackerToken = "", defenderToken = "";
        private readonly string root;
        private readonly string? database;
        private readonly bool legacyArtifacts;
        public Fixture(string root, Func<string, string, SaveSnapshotPackage> fixture, bool persistent, bool legacyArtifacts)
        {
            Artifacts = Path.Combine(root, "artifacts");
            this.root = root;
            this.legacyArtifacts = legacyArtifacts;
            if (persistent) { database = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(database); }
            Open();
            Raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new EventParty("userA", "colonyA"), new EventParty("userB", "colonyB"),
                "raid-auth", false, new RaidEventPayload("defender", null, DateTimeOffset.UtcNow, null, null), DateTimeOffset.UtcNow,
                new EventTargetContext("WorldObject_0", "Map_0", 1, EventLandingMode.MapEdge));
            State.Ledger.Append(Raid);
            Package = fixture("evidence", Raid.EventId);
            var defender = Package with { Envelope = Package.Envelope with { Identity = new SnapshotIdentity("userB", "colonyB", "defender"), NextLineageToken = "defender-next" } };
            Store.Inner.StoreLatest(defender, defender.Index, DateTimeOffset.UtcNow);
        }
        public void Open()
        {
            Store = new ObservedStore(Path.Combine(root, "snapshots"));
            ArtifactStore = new ObservedArtifacts(Artifacts);
            State = new ClashOfRimNetworkState(snapshotStore: Store, snapshotPostUploadArtifacts: legacyArtifacts ? new LegacyArtifacts(ArtifactStore.Inner) : ArtifactStore,
                ledger: database is null ? null : new SqliteAuthoritativeEventLedger(database),
                snapshotCommits: new SnapshotCommitService(Path.Combine(root, "intents")),
                playerRegistry: database is null ? null : new PlayerRegistry(new SqlitePlayerRegistryStore(database), null),
                snapshotPostUploadJobs: database is null ? null : new SnapshotPostUploadJobRegistry(new SqliteSnapshotPostUploadJobStore(database)),
                plugins: new AIRsLight.ClashOfRim.Network.Plugins.ServerPluginRegistry().WithBuiltInPlugins([
                    new("test.evidence", "evidence", "1", "test", "", []) { SnapshotPostUploadProcessors = [
                        new EvidenceProcessor(250, context => BeforeSchedule?.Invoke(context)),
                        new EvidenceProcessor(350, context => AfterSchedule?.Invoke(context))] }]));
            attackerToken = Token("userA", "colonyA"); defenderToken = Token("userB", "colonyB");
        }
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + database);
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        private string Token(string user, string colony)
        {
            State.LoginSessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out string session);
            return State.AuthTokens.Issue(user, user, colony, session, DateTimeOffset.UtcNow);
        }
        public string StateEvidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), ledger = State.Ledger.ListAll(),
            jobs = State.SnapshotPostUploadJobs.ListAll(), artifacts = Directory.EnumerateFiles(Artifacts, "*.snapshot.gz").Order().Select(path =>
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray(), players = new[] { State.Players.FindByUserId("userA"), State.Players.FindByUserId("userB") } });
        public ConfirmEventApplicationResponse Invoke(string identity = "attacker", SaveSnapshotPackage? package = null, string result = "Applied", Action<System.Text.Json.Nodes.JsonObject>? mutate = null)
        {
            package ??= Package;
            bool defender = identity is "defender" or "delegated";
            string owner = defender ? "userB" : "userA", colony = defender ? "colonyB" : "colonyA";
            var metadata = ProtocolDtoMapper.ToMetadataDto(package);
            if (identity is "defender" or "delegated" or "metadata") metadata = ProtocolDtoMapper.ToMetadataDto(package with { Envelope = Package.Envelope with { Identity = new SnapshotIdentity("userB", "colonyB", "evidence") } });
            var request = new ConfirmEventApplicationMetadataRequest("request", Raid.EventId, null, owner, colony, "before", metadata, result,
                defender && identity != "delegated" ? defenderToken : attackerToken);
            object body = request;
            if (mutate is not null) { var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request))!.AsObject(); mutate(node); body = node; }
            return SnapshotEndpointTestClient.Invoke<ConfirmEventApplicationResponse>("ConfirmEventApplication", State,
                _ => body, package.Payload,
                new ProtocolIdentity(identity == "header" ? "userB" : identity == "delegated" ? "userA" : owner,
                    identity == "header" ? "colonyB" : identity == "delegated" ? "colonyA" : colony, null),
                identity == "header" || defender && identity != "delegated" ? defenderToken : attackerToken).GetAwaiter().GetResult();
        }
    }
    private sealed class ObservedStore(string root) : IPreparedColonySnapshotPackageStore
    {
        public FileColonySnapshotIndexStore Inner { get; } = new(root);
        public Action? BeforePrepare, BeforeRead;
        public string? Fault;
        public int PreparedWrites, RawWrites;
        public PreparedSnapshotPayload PreparePayload(byte[] bytes) { BeforePrepare?.Invoke(); return Inner.PreparePayload(bytes); }
        public void StorePrepared(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at, PreparedSnapshotPayload prepared) {
            if (Fault == "before") throw new IOException("before publication");
            Inner.StorePrepared(p, i, at, prepared); PreparedWrites++;
            if (Fault == "after") throw new IOException("after publication");
        }
        public void StoreLatest(SaveSnapshotPackage p, SaveSnapshotIndex i, DateTimeOffset at) { RawWrites++; Inner.StoreLatest(p, i, at); }
        public void StoreLatest(LatestSnapshotRecord record) => Inner.StoreLatest(record);
        public LatestSnapshotRecord? GetLatest(string owner, string colony) => Inner.GetLatest(owner, colony);
        public SaveSnapshotPackage? GetLatestPackage(string owner, string colony) { BeforeRead?.Invoke(); return Inner.GetLatestPackage(owner, colony); }
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => Inner.ListLatest();
        public bool RemoveLatest(string owner, string colony) => Inner.RemoveLatest(owner, colony);
    }
}
