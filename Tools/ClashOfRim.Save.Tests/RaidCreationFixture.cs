using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class RaidCreationFixture
{
    private readonly string root;
    private string token = "";
    public string Database => Path.Combine(root, "server.sqlite");
    public string Journal => Path.Combine(root, "intents");
    public bool Guarded { get; }
    public SaveSnapshotPackage Package { get; }
    public ClashOfRimNetworkState State { get; private set; } = null!;
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;

    private RaidCreationFixture(string root, Func<string, SaveSnapshotPackage> factory, bool guarded)
    {
        this.root = root; Guarded = guarded;
        ServerDatabaseMigrator.Migrate(Database);
        Open();
        Store.Gate = null;
        Package = RaidTestPreparation.Prepare(Store, State.DiplomacyRelations, State.MercenaryGuards, factory, guarded);
        Store.Gate = State.RaidSettlementSnapshotMutationGate;
        Store.Reset();
    }

    public void Open()
    {
        Store = new(Path.Combine(root, "snapshots"));
        State = new(snapshotStore: Store, ledger: new SqliteAuthoritativeEventLedger(Database), mercenaryGuards: RaidTestPreparation.OpenGuards(Database),
            snapshotCommits: new SnapshotCommitService(Journal), serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
        Store.Gate = State.RaidSettlementSnapshotMutationGate;
        token = SessionTestAuthentication.Token(State, "userA", "colonyA");
        RaidTestPreparation.SetHostile(State.DiplomacyRelations);
    }

    public EventCreationResponse Call(Action<JsonObject>? mutate = null, byte[]? bytes = null) => RaidCreationTestClient.Invoke(State,
        bytes is null ? Package : Package with { Payload = bytes }, Guarded, token: token, mutate: mutate).GetAwaiter().GetResult();

    public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
        guard = State.MercenaryGuards.FindByIdempotencyKey("guard"), replacement = State.MercenaryGuards.FindByIdempotencyKey("replacement"),
        a = State.EventNotifications.GetVersion("userA"), b = State.EventNotifications.GetVersion("userB") });

    public SnapshotCommitIntent Seed()
    {
        Store.FailPublication = true;
        try { Call(); throw new Exception("Missing publication failure."); }
        catch (IOException) { }
        finally { Store.FailPublication = false; }
        return JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(Directory.GetFiles(Journal, "*.json").Single()))!;
    }

    public static void With(Func<string, SaveSnapshotPackage> factory, bool guarded, Action<RaidCreationFixture> run) =>
        SqliteTestDirectory.With("cor-raid-creation-", root => run(new(root, factory, guarded)));
}
