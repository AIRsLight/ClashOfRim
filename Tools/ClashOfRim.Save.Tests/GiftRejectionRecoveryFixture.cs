using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static GiftRejectionCase;
using static GiftRejectionTestClient;
using static SessionTestAuthentication;
using static SnapshotTestAssertions;

internal sealed class GiftRejectionRecoveryFixture
{
    private readonly string root;
    public string Database => Path.Combine(root, "server.sqlite");
    private string Journal => Path.Combine(root, "intents");
    public SaveSnapshotPackage Package { get; }
    public AuthoritativeEvent ForeignGift { get; }
    public GiftRejectionCase Gift { get; }
    public AuthoritativeEvent Source => Gift.Source;
    public ClashOfRimNetworkState State { get; private set; } = null!;
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;
    public string AuthToken { get; private set; } = "";
    public Action? AfterPublication;
    private GiftRejectionRecoveryFixture(string root, Func<string, SaveSnapshotPackage> factory)
    {
        this.root = root; ServerDatabaseMigrator.Migrate(Database); Open(); Gift = new(State.Ledger);
        var package = factory("foreign");
        Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
            PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
        ForeignGift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
            "rejection-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), At);
    }
    public void Open()
    {
        Store = new(Path.Combine(root, "snapshots"));
        State = new(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
            playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
            plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.gift-recovery", "recovery", "1", "test", "", [])
                { SnapshotPostUploadProcessors = [new SnapshotPublicationProbe("test.gift-recovery", "foreign", () => AfterPublication?.Invoke())] }]));
        Store.Gate = State.RaidSettlementSnapshotMutationGate; AuthToken = Token(State, "receiver", "away"); AfterPublication = null;
    }
    public RejectGiftResponse Call() => Reject(State, Request(Source, AuthToken));
    public void Seed(bool prepared = false)
    {
        var storage = prepared ? Store.PreparePayload(Package.Payload) : null;
        using (State.EnterSnapshotMutation())
        {
            Store.FailPublication = true;
            try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                new(Package.Envelope.Identity, Package.Envelope, Package.Index, At), ForeignGift, (_, _) => { }, _ => { }, preparedStoragePayload: storage); }
            catch (IOException) { }
            finally { Store.FailPublication = false; }
        }
        Check(State.SnapshotCommits.HasPending, "Could not seed gift recovery.");
    }
    public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
        players = State.Players.FindByUserId("foreign"), journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(),
        notifications = new[] { State.EventNotifications.GetVersion("sender"), State.EventNotifications.GetVersion("receiver"), State.EventNotifications.GetVersion("recipient") } });
    public void AssertReleased() => Task.Run(() =>
    {
        Check(Monitor.TryEnter(State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Gift recovery leaked its mutation gate.");
        Monitor.Exit(State.RaidSettlementSnapshotMutationGate);
    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    public static void With(Func<string, SaveSnapshotPackage> factory, Action<GiftRejectionRecoveryFixture> run)
        => SqliteTestDirectory.With("cor-gift-endpoint-", root => run(new(root, factory)));
}
