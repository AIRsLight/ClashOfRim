using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static SupportClosureTestData;
using static SnapshotTestAssertions;

internal sealed class SupportClosureRecoveryFixture
{
    private readonly string root;
    public string Kind { get; }
    public string Database => Path.Combine(root, "server.sqlite");
    public string Journal => Path.Combine(root, "intents");
    public SaveSnapshotPackage Package { get; }
    public AuthoritativeEvent Gift { get; }
    public AuthoritativeEvent Source { get; } = SupportClosureTestData.Source();
    public ClashOfRimNetworkState State { get; private set; } = null!;
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;
    public SupportClosureTestClient Client { get; private set; } = null!;
    public string AuthToken { get; private set; } = "";
    public Action? AfterPublication;
    private SupportClosureRecoveryFixture(string root, Func<string, SaveSnapshotPackage> factory, string kind)
    {
        this.root = root; Kind = kind; ServerDatabaseMigrator.Migrate(Database); Open(); State.Ledger.Append(Source);
        var package = factory("foreign");
        Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
            PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
        Gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
            "closure-foreign", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), OriginalTime);
    }
    public void Open()
    {
        Store = new(Path.Combine(root, "snapshots"));
        State = new(ledger: new SqliteAuthoritativeEventLedger(Database), snapshotStore: Store,
            playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(Database), null), snapshotCommits: new SnapshotCommitService(Journal),
            plugins: new ServerPluginRegistry().WithBuiltInPlugins([new("test.closure-recovery", "recovery", "1", "test", "", [])
                { SnapshotPostUploadProcessors = [new SnapshotPublicationProbe("test.closure-recovery", "foreign", () => AfterPublication?.Invoke())] }]));
        Store.Gate = State.RaidSettlementSnapshotMutationGate;
        Client = SupportClosureTestClient.Create(State); AuthToken = Client.Token("userA"); AfterPublication = null;
    }
    public (bool Accepted, bool Created, string? ReturnId) Call() => Client.Call(Kind, token: AuthToken, authenticate: false);
    public void Seed(bool prepared = false)
    {
        var storage = prepared ? Store.PreparePayload(Package.Payload) : null;
        using (State.EnterSnapshotMutation())
        {
            Store.FailPublication = true;
            try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                new(Package.Envelope.Identity, Package.Envelope, Package.Index, OriginalTime), Gift, (_, _) => { }, _ => { }, preparedStoragePayload: storage); }
            catch (IOException) { }
            finally { Store.FailPublication = false; }
        }
        Check(State.SnapshotCommits.HasPending, "Could not seed support recovery.");
    }
    public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
        players = State.Players.FindByUserId("foreign"), journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(),
        notifications = new[] { State.EventNotifications.GetVersion("owner"), State.EventNotifications.GetVersion("userA"), State.EventNotifications.GetVersion("recipient") } });
    public void AssertClosed(string kind)
        => SupportClosureTestAssertions.Closed(State.Ledger.ListAll().Where(e => e.EventId != Gift.EventId).ToArray(), kind);
    public void AssertReleased() => GateReleased(State.RaidSettlementSnapshotMutationGate, "Support recovery leaked its mutation gate.");

    public static void With(Func<string, SaveSnapshotPackage> factory, string kind, Action<SupportClosureRecoveryFixture> run)
        => SqliteTestDirectory.With("cor-support-closure-", root => run(new(root, factory, kind)));
}
