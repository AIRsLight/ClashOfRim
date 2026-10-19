using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static EventFailureGraph;
using static EventFailureTestClient;
using static EventLedgerTestEvidence;
using static SnapshotTestAssertions;

internal sealed class EventFailureRecoveryFixture
{
    private readonly string root;
    private string Database => Path.Combine(root, "server.sqlite");
    private string Journal => Path.Combine(root, "intents");
    public EventFailureGraph Graph { get; }
    public string Kind => Graph.Kind;
    public string AuthUser { get; private set; } = "buyer";
    public string AuthToken { get; private set; } = "";
    public AuthoritativeEvent Source => Graph.Root;
    public AuthoritativeEvent ForeignGift { get; }
    public SaveSnapshotPackage Package { get; }
    public string InitialBusiness { get; }
    public ClashOfRimNetworkState State { get; private set; } = null!;
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;
    public Action? AfterPublication;
    private EventFailureRecoveryFixture(string root, Func<string, SaveSnapshotPackage> factory, string kind, bool delegated = false)
    {
        this.root = root; ServerDatabaseMigrator.Migrate(Database); Open(); Graph = new(State.Ledger, kind);
        AuthUser = delegated ? Source.Actor.UserId : Source.Target.UserId;
        AuthToken = Token(State, AuthUser, AuthUser + "-colony");
        var package = factory("foreign");
        Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
            PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
        ForeignGift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
            "failure-report-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), Original);
        InitialBusiness = Business();
    }
    public void Open()
    {
        Store = new(Path.Combine(root, "snapshots"));
        State = new(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
            playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
            plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.failure-recovery", "recovery", "1", "test", "", [])
                { SnapshotPostUploadProcessors = [new SnapshotPublicationProbe("test.failure-recovery", "foreign", () => AfterPublication?.Invoke())] }]));
        Store.Gate = State.RaidSettlementSnapshotMutationGate; AuthToken = Token(State, AuthUser, AuthUser + "-colony"); AfterPublication = null;
    }
    public ReportEventApplicationFailureResponse Call() => Report(State, Source, token: AuthToken);
    public void SeedIntent(bool prepared = false)
    {
        var storage = prepared ? Store.PreparePayload(Package.Payload) : null;
        using (State.EnterSnapshotMutation())
        {
            Store.FailPublication = true;
            try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                new(Package.Envelope.Identity, Package.Envelope, Package.Index, Original), ForeignGift, (_, _) => { }, _ => { }, preparedStoragePayload: storage); }
            catch (IOException) { }
            finally { Store.FailPublication = false; }
        }
        Check(State.SnapshotCommits.HasPending, "Could not seed failure report recovery.");
    }
    public void ChangeParty(string column)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Database); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = $"update events set {column} = 'other' where event_id = $id";
        command.Parameters.AddWithValue("$id", Source.EventId); command.ExecuteNonQuery();
    }
    public string Business() => Snapshot(State.Ledger.ListAll().Where(e => e.IdempotencyKey != "failure-report-foreign"));
    public string Notifications() => string.Join("/", new[] { "seller", "buyer", "observer" }.Select(State.EventNotifications.GetVersion));
    public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = Business(),
        foreignGift = State.Ledger.FindByIdempotencyKey("failure-report-foreign"), players = State.Players.FindByUserId("foreign"),
        journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(), notifications = Notifications() });
    public void AssertRecovered() => Check(Store.Writes == 1 && !State.SnapshotCommits.HasPending
        && Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
        && Same(ForeignGift, State.Ledger.Find(ForeignGift.EventId)!), "Failure report did not recover the original intent once.");
    public void AssertReleased() => Task.Run(() =>
    { Check(Monitor.TryEnter(State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Failure report leaked its gate."); Monitor.Exit(State.RaidSettlementSnapshotMutationGate); })
        .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    public static void With(Func<string, SaveSnapshotPackage> factory, string kind,
        Action<EventFailureRecoveryFixture> run, bool delegated = false)
        => SqliteTestDirectory.With("cor-failure-report-", root => run(new(root, factory, kind, delegated)));
}
