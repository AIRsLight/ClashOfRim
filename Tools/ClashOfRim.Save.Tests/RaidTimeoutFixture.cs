using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static SnapshotTestAssertions;

internal sealed class RaidTimeoutFixture
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "server.sqlite");
    public string Journal => Path.Combine(Root, "intents");
    public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddHours(3);
    public AuthoritativeEvent Raid { get; }
    public SaveSnapshotPackage Original { get; }
    public FileColonySnapshotIndexStore Files => Store.Inner;
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;
    public SnapshotArtifactTestStore Artifacts { get; private set; } = null!;
    public ClashOfRimNetworkState State { get; private set; } = null!;
    private RaidTimeoutFixture(string root, SaveSnapshotPackage original)
    {
        Root = root; Original = original;
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
        if (Artifacts is not null) AssertArtifactsReleased();
        Store = new SnapshotRecoveryTestStore(Path.Combine(Root, "snapshots"));
        Artifacts = new SnapshotArtifactTestStore(Path.Combine(Root, "artifacts"));
        State = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
            playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
            snapshotPostUploadJobs: new SnapshotPostUploadJobRegistry(new SqliteSnapshotPostUploadJobStore(Database)),
            snapshotPostUploadArtifacts: Artifacts);
    }
    public void Reconcile() => State.RaidTimeouts.Reconcile(Now);
    public void Sql(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public (SaveSnapshotPackage Package, AuthoritativeEvent Event) CreateForeignIntent()
    {
        var template = Files.GetLatestPackage("defender", "defender-colony")!;
        var package = template with { Envelope = template.Envelope with { Identity = new("foreign", "foreign-colony", "foreign-snapshot"),
            PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
        var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
            "foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign-snapshot")], "foreign"), Now);
        return (package, gift);
    }
    public void SeedForeign((SaveSnapshotPackage Package, AuthoritativeEvent Event) intent)
    {
        Store.FailPublication = true; Store.FailAfterPublication = false;
        var snapshot = new LatestSnapshotRecord(intent.Package.Envelope.Identity, intent.Package.Envelope, intent.Package.Index, Now);
        try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, intent.Package, snapshot, intent.Event, (_, _) => { }, _ => { }); }
        catch (IOException) { }
        finally { Store.FailPublication = false; }
        Check(State.SnapshotCommits.HasPending, "Could not seed unrelated recovery during maintenance.");
    }
    public string JournalEvidence() => JsonSerializer.Serialize(new { snapshots = Files.ListLatest(), events = State.Ledger.ListAll(),
        jobs = State.SnapshotPostUploadJobs.ListAll(), players = new[] { State.Players.FindByUserId("attacker"), State.Players.FindByUserId("foreign") },
        journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray() });

    public static void With(SaveSnapshotPackage original, Action<RaidTimeoutFixture> run)
        => SqliteTestDirectory.With("cor-timeout-", root =>
        {
            var fixture = new RaidTimeoutFixture(root, original);
            try { run(fixture); }
            finally { fixture.AssertArtifactsReleased(); }
        });

    private void AssertArtifactsReleased()
        => Artifacts.AssertReleased("Timeout fixture leaked artifact revision handles.");
}
