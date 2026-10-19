using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using static SnapshotTestAssertions;

internal sealed class RaidCleanupFixture
{
    private readonly string root;
    private readonly bool timeout;
    public string Database => Path.Combine(root, "server.sqlite");
    public string Journal => Path.Combine(root, "intents");
    public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddHours(3);
    public SaveSnapshotPackage Original { get; }
    public SaveSnapshotPackage Cleaned { get; }
    public RaidCleanupSnapshotMutation Mutation { get; }
    public AuthoritativeEvent Raid { get; private set; }
    public AuthoritativeEvent Support { get; }
    public ClashOfRimNetworkState State { get; private set; } = null!;
    public FileColonySnapshotIndexStore Files => Store.Inner;
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;
    private RaidCleanupFixture(string root, SaveSnapshotPackage original, bool timeout)
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
        Check(RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(Original, "cleaned", Now, out var cleaned,
            out RaidAttackerSnapshotCleanupResult result), "Cleanup fixture was not removable.");
        Cleaned = cleaned;
        Mutation = State.RaidBattleCleanup.BuildMutation(Raid, (RaidEventPayload)Raid.Payload,
            Original, Cleaned, result, Now, timeout ? "timeout" : "map-cleanup");
    }
    public void Open()
    {
        Store = new(Path.Combine(root, "snapshots"));
        DownloadReader = "attacker"; downloadToken = ""; downloadScope = null;
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

    public string DownloadReader { get; private set; } = "attacker";
    private string downloadToken = "";
    private string? downloadScope;
    public void PrepareDownloadAccess(bool allied)
    {
        DownloadReader = allied ? "reader" : "attacker";
        string colony = allied ? "reader-colony" : "colony-a";
        downloadToken = SessionTestAuthentication.Token(State, DownloadReader, colony);
        downloadScope = allied ? "FriendlyObservation" : null;
        if (allied) SetDownloadRelation(DiplomacyRelationRegistry.RelationAlly);
    }
    public void SetDownloadRelation(string relation) => State.DiplomacyRelations.SetRelationKind("reader", "reader-colony", "attacker", "colony-a",
        relation, null, DateTimeOffset.UtcNow);
    public SnapshotDownloadTestClient.Reply Download(bool payload) =>
        SnapshotDownloadTestClient.Download(State.SnapshotDownloads, Files, payload, downloadToken, downloadScope);

    public (SaveSnapshotPackage Package, AuthoritativeEvent Event) DownloadForeignIntent()
    {
        var package = Cleaned with { Envelope = Cleaned.Envelope with { Identity = new("foreign", "foreign-colony", "foreign-snapshot"),
            PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
        var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
            "download-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign-snapshot")], "foreign"), Now);
        return (package, gift);
    }
    public void SeedDownloadRecovery((SaveSnapshotPackage Package, AuthoritativeEvent Event) intent, bool own)
    {
        Store.FailPublication = true;
        try
        {
            if (own) State.SnapshotCommits.CommitRaidCleanup(Store, State.BankLoans, State.Ledger, State.Players,
                Cleaned, Mutation, Now, _ => { });
            else State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, intent.Package,
                new(intent.Package.Envelope.Identity, intent.Package.Envelope, intent.Package.Index, Now), intent.Event, (_, _) => { }, _ => { });
        }
        catch (IOException) { }
        finally { Store.FailPublication = false; }
        Check(State.SnapshotCommits.HasPending, "Could not seed download journal recovery.");
    }
    public string DownloadRecoveryEvidence() => JsonSerializer.Serialize(new { snapshots = Files.ListLatest(), events = State.Ledger.ListAll(),
        players = new[] { State.Players.FindByUserId("attacker"), State.Players.FindByUserId("foreign") },
        journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray() });

    public void RefreshRaid() => Raid = State.Ledger.Find(Raid.EventId)!;

    public static void With(SaveSnapshotPackage original, bool timeout, Action<RaidCleanupFixture> run) =>
        SqliteTestDirectory.With("cor-raid-cleanup-", root => run(new(root, original, timeout)));
}
