using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SnapshotTestAssertions;

internal sealed class RaidEvidenceFixture
{
    public string Artifacts { get; }
    public SaveSnapshotPackage Package { get; }
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;
    public SnapshotArtifactTestStore ArtifactStore { get; private set; } = null!;
    public PublicationFaults Publication { get; private set; } = null!;
    public Action<SnapshotPostUploadContext>? BeforeSchedule, AfterSchedule;
    public ClashOfRimNetworkState State { get; private set; } = null!;
    public AuthoritativeEvent Raid { get; }
    private string attackerToken = "", defenderToken = "";
    private readonly string root;
    private readonly string? database;
    private readonly bool legacyArtifacts;
    private RaidEvidenceFixture(string root, Func<string, string, SaveSnapshotPackage> fixture, bool persistent, bool legacyArtifacts)
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
        if (ArtifactStore is not null) AssertArtifactsReleased();
        Publication = new PublicationFaults();
        Store = new SnapshotRecoveryTestStore(Path.Combine(root, "snapshots"))
        {
            BeforePublication = Publication.Before,
            AfterPublication = Publication.After
        };
        ArtifactStore = new SnapshotArtifactTestStore(Artifacts);
        State = new ClashOfRimNetworkState(snapshotStore: Store, snapshotPostUploadArtifacts: legacyArtifacts ? new SnapshotArtifactStoreView(ArtifactStore.Inner) : ArtifactStore,
            ledger: database is null ? null : new SqliteAuthoritativeEventLedger(database),
            snapshotCommits: new SnapshotCommitService(Path.Combine(root, "intents")),
            playerRegistry: database is null ? null : new PlayerRegistry(new SqlitePlayerRegistryStore(database), null),
            snapshotPostUploadJobs: database is null ? null : new SnapshotPostUploadJobRegistry(new SqliteSnapshotPostUploadJobStore(database)),
            plugins: new ServerPluginRegistry().WithBuiltInPlugins([
                new("test.evidence", "evidence", "1", "test", "", []) { SnapshotPostUploadProcessors = [
                    new SnapshotPublicationProbe("test.evidence.250", 250, kind => kind == SnapshotPostUploadKind.RaidSettlementEvidence, context => BeforeSchedule?.Invoke(context)),
                    new SnapshotPublicationProbe("test.evidence.350", 350, kind => kind == SnapshotPostUploadKind.RaidSettlementEvidence, context => AfterSchedule?.Invoke(context))] }]));
        attackerToken = SessionTestAuthentication.Token(State, "userA", "colonyA");
        defenderToken = SessionTestAuthentication.Token(State, "userB", "colonyB");
    }
    public void Sql(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public string StateEvidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), ledger = State.Ledger.ListAll(),
        jobs = State.SnapshotPostUploadJobs.ListAll(), artifacts = Directory.EnumerateFiles(Artifacts, "*.snapshot.gz").Order().Select(path =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray(), players = new[] { State.Players.FindByUserId("userA"), State.Players.FindByUserId("userB") } });
    public ConfirmEventApplicationResponse Invoke(string identity = "attacker", SaveSnapshotPackage? package = null,
        string result = "Applied", Action<JsonObject>? mutate = null)
        => RaidEvidenceTestClient.Invoke(State, Raid.EventId, Package, attackerToken, defenderToken, identity, package, result, mutate);

    public void EraseJobBinding()
    {
        var job = State.SnapshotPostUploadJobs.ListAll().Single();
        var payload = JsonNode.Parse(job.PayloadJson)!.AsObject(); payload.Remove("requestHash");
        new SqliteSnapshotPostUploadJobStore(database!).Upsert(job with { PayloadJson = payload.ToJsonString() });
    }
    public Action PrepareCompletion(bool conflict)
    {
        Publication.PreparedFault = "before";
        try { Complete(); throw new Exception("Missing interruption."); } catch (IOException) { }
        string path = Directory.GetFiles(Path.Combine(root, "intents"), "*.json").Single();
        var intent = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(path))!;
        File.Delete(path); Open();
        var mutation = intent.RaidSettlement!;
        if (conflict) mutation = mutation with { Settlement = mutation.Settlement with
            { Payload = ((RaidEventPayload)mutation.Settlement.Payload) with { EvidenceRequestHash = "v1:other-request" } } };
        var prepared = Store.PreparePayload(intent.Package.Payload);
        return () => State.SnapshotCommits.CommitRaidSettlement(Store, State.BankLoans, State.Ledger, State.Players,
            intent.Package, mutation, _ => { }, prepared);
    }
    public string Binding() => RaidEvidenceTestClient.Binding(Raid.EventId, Package);
    public RaidEventPayload Receipt() => (RaidEventPayload)State.Ledger.FindByIdempotencyKey("raid-settlement:" + Raid.EventId)!.Payload;
    public void Complete()
    {
        var job = State.SnapshotPostUploadJobs.ListAll().Single();
        var payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
        var evidence = ArtifactStore.Inner.Read(payload.EvidenceArtifactId)!;
        var result = RaidSettlementOperationExecutor.Execute(State, payload, evidence, DateTimeOffset.UtcNow);
        Check(result.Kind is RaidSettlementOperationResultKind.Completed or RaidSettlementOperationResultKind.AlreadyCompleted,
            "Fixture settlement failed: " + result.Message);
        State.SnapshotPostUploadJobs.MarkCompleted(job.JobId);
        ArtifactStore.Inner.Delete(payload.EvidenceArtifactId);
    }

    public string RecoveryJournal => Path.Combine(root, "intents");
    public (SaveSnapshotPackage Package, AuthoritativeEvent Event) ForeignIntent()
    {
        var template = Store.Inner.GetLatestPackage("userB", "colonyB")!;
        var package = template with { Envelope = template.Envelope with { Identity = new("foreign", "foreign-colony", "foreign-snapshot"),
            PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
        var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
            "foreign-gift", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign-snapshot")], "foreign"), DateTimeOffset.UtcNow);
        return (package, gift);
    }
    public void SeedRecovery((SaveSnapshotPackage Package, AuthoritativeEvent Event) intent)
    {
        Publication.FailRaw = true;
        var snapshot = new LatestSnapshotRecord(intent.Package.Envelope.Identity, intent.Package.Envelope, intent.Package.Index, DateTimeOffset.UtcNow);
        try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, intent.Package, snapshot, intent.Event, (_, _) => { }, _ => { }); }
        catch (IOException) { }
        finally { Publication.FailRaw = false; }
        Check(State.SnapshotCommits.HasPending, "Could not seed journal recovery for evidence confirmation.");
    }
    public string RecoveryEvidence() => JsonSerializer.Serialize(new { state = StateEvidence(),
        journal = Directory.GetFiles(RecoveryJournal, "*.json").Order().Select(File.ReadAllText).ToArray() });
    public void AssertRecoveryReleased()
    {
        Check(!Directory.EnumerateFiles(Artifacts, "*.tmp").Any(), "Journal recovery leaked prepared evidence files.");
        GateReleased(State.RaidSettlementSnapshotMutationGate, "Evidence recovery leaked the mutation gate.");
    }

    public static void With(Func<string, string, SaveSnapshotPackage> factory, Action<RaidEvidenceFixture> run,
        bool persistent = false, bool legacyArtifacts = false)
        => SqliteTestDirectory.With("cor-raid-auth-", root =>
        {
            var fixture = new RaidEvidenceFixture(root, factory, persistent, legacyArtifacts);
            try { run(fixture); }
            finally { fixture.AssertArtifactsReleased(); }
        });

    private void AssertArtifactsReleased()
        => ArtifactStore.AssertReleased("Online request leaked artifact revision handles.");

    // These faults belong to one opened store. Raw recovery and prepared publication remain distinct paths.
    internal sealed class PublicationFaults
    {
        public string? PreparedFault;
        public bool FailRaw;
        public void Before(bool prepared)
        {
            if (prepared && PreparedFault == "before") throw new IOException("before publication");
            if (!prepared && FailRaw) throw new IOException("recovery publication interrupted");
        }
        public void After(bool prepared)
        {
            if (prepared && PreparedFault == "after") throw new IOException("after publication");
        }
    }
}
