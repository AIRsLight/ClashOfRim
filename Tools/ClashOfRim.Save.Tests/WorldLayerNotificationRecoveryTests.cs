using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class WorldLayerNotificationRecoveryTests
{
    private const string Layer = BiotechCompatibilityKeys.WorldPollution;
    private static readonly DateTimeOffset Accepted = DateTimeOffset.UnixEpoch.AddDays(4);
    private static readonly WorldConfigurationExtensionService Extensions = new([new BiotechWorldPollutionExtensionProvider()]);

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool legacy in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string failure in new[] { "invalid-batch", "configuration", "events-first", "events-second", "after-batch", "ack" })
            WithFixture(fixture, legacy, f => VerifyRecovery(f, reopen, failure));
        WithFixture(fixture, false, VerifyBackground);
        WithFixture(fixture, true, VerifyEmptyRecipients);
        WithFixture(fixture, false, VerifyConflict);
        foreach (bool legacy in new[] { false, true })
        foreach (string corrupt in new[] { "version", "event", "json" })
            WithFixture(fixture, legacy, f => VerifyCorruption(f, corrupt));
    }

    private static void VerifyRecovery(Fixture f, bool reopen, string failure)
    {
        f.Fail(failure);
        ExpectFailure(f.Project);
        bool configured = failure != "configuration";
        int expectedEvents = failure is "after-batch" or "ack" ? 3 : 0;
        foreach (var world in new[] { f.State.WorldConfiguration, f.OpenWorld() })
        {
            Require(world.HasPendingLayerNotifications == configured, "configuration/outbox was only partially committed");
            float pollution = Extensions.ReadTileFloatLayer(world.Current!.Extensions, Layer).Single(item => item.Tile == 1).Value;
            Require(configured ? pollution > 0.7f : pollution == 0.2f, "configuration cache diverged after failure");
        }
        Require(f.Ledger.ListAll().Count == expectedEvents, "notification batch was partially committed");
        if (failure != "ack") Require(f.State.EventNotifications.GetVersion("owner") == 0, "notification was sent before commit");
        var original = f.State.WorldConfiguration.ListPendingLayerNotifications().SingleOrDefault();
        f.ClearFailure();
        if (configured)
        {
            // Recovery must use the original targets even after sites/configuration change.
            f.State.WorldConfiguration.RemovePlayerColonySites("owner", "abandoned");
            Require(f.OpenWorld().HasPendingLayerNotifications, "unrelated world write erased pending work");
            foreach (var item in f.Ledger.ListAll())
            {
                f.Ledger.MarkDelivered(item.EventId, "delivered", Accepted.AddMinutes(1));
                f.Ledger.MarkApplied(item.EventId, "applied", Accepted.AddMinutes(2));
            }
        }
        var before = f.Ledger.ListAll().ToDictionary(item => item.EventId);
        if (reopen) f.Open();
        long ownerVersion = f.State.EventNotifications.GetVersion("owner");
        long worldVersion = f.State.WorldConfigurationNotifications.GetVersion("observer");
        if (!configured) f.Project();
        else ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
        VerifyCompleted(f);
        Require(f.State.EventNotifications.GetVersion("owner") > ownerVersion
            && f.State.WorldConfigurationNotifications.GetVersion("observer") > worldVersion, "recovery omitted a persisted recipient wake-up");
        Require(before.All(pair => Same(pair.Value, f.Ledger.Find(pair.Key)!)), "recovery reset consumed notification state");
        if (original is not null)
            Require(original.Notifications.All(item => f.Ledger.Find(item.EventId) is { } actual
                && actual.Target == item.Target && actual.CreatedAtUtc == item.CreatedAtUtc && SamePayload(actual, item)),
                "recovery recalculated notification identity, text, time or recipients");
        long version = f.State.EventNotifications.GetVersion("owner");
        f.Project();
        Require(f.State.EventNotifications.GetVersion("owner") == version && f.Ledger.ListAll().Count == 3,
            "completed replay duplicated notifications or repeated the increase");
    }

    private static void VerifyBackground(Fixture f)
    {
        f.Fail("events-first"); ExpectFailure(f.Project); f.ClearFailure(); f.Open();
        using var background = new SnapshotPostUploadBackgroundService(f.State, new SnapshotPostUploadProcessorSource(() => []));
        background.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Require(SpinWait.SpinUntil(() => !f.State.WorldConfiguration.HasPendingLayerNotifications, TimeSpan.FromSeconds(10)),
                "background did not recover without another upload");
            VerifyCompleted(f);
        }
        finally { background.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private static void VerifyEmptyRecipients(Fixture f)
    {
        f.Distance.Value = 100;
        f.Fail("ack"); ExpectFailure(f.Project);
        Require(f.State.WorldConfiguration.ListPendingLayerNotifications().Single().Notifications.Count == 0,
            "far-away colonies received notifications");
        f.ClearFailure(); f.Open();
        ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
        Require(!f.State.WorldConfiguration.HasPendingLayerNotifications && f.Ledger.ListAll().Count == 0
            && f.State.WorldConfigurationNotifications.GetVersion("observer") == 1,
            "empty event batch lost the world-change wake-up");
    }

    private static void VerifyConflict(Fixture f)
    {
        f.Fail("events-first"); ExpectFailure(f.Project); f.ClearFailure();
        var work = f.State.WorldConfiguration.ListPendingLayerNotifications().Single();
        f.Ledger.Append(work.Notifications[0] with { Target = new EventParty("wrong", "wrong") });
        ExpectFailure(() => ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State));
        Require(f.OpenWorld().HasPendingLayerNotifications && f.Ledger.ListAll().Count == 1,
            "conflict discarded work or partially appended the notification batch");
        Require(f.State.EventNotifications.GetVersion("owner") == 0, "conflict woke recipients");
    }

    private static void VerifyCorruption(Fixture f, string kind)
    {
        f.Fail("events-first"); ExpectFailure(f.Project); f.ClearFailure();
        var array = JsonSerializer.SerializeToNode(f.State.WorldConfiguration.ListPendingLayerNotifications())!.AsArray();
        if (kind == "version") array[0]!["Version"] = 999;
        if (kind == "event") array[0]!["Notifications"]![0]!["Target"]!["UserId"] = "";
        if (f.Legacy)
        {
            var document = JsonNode.Parse(f.Json.Value!)!;
            document["PendingLayerNotifications"] = kind == "json" ? JsonValue.Create("not a list") : array;
            f.Json.Value = document.ToJsonString();
        }
        else
        {
            using var connection = new SqliteConnection("Data Source=" + f.Database); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "update server_keyed_json_records set content_json = $json where item_key = 'pending-layer-notifications';";
            command.Parameters.AddWithValue("$json", kind == "json" ? "not json" : array.ToJsonString()); command.ExecuteNonQuery();
        }
        bool rejected = false;
        try { f.OpenWorld(); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "malformed or future outbox was silently discarded on load");
    }

    private static void VerifyCompleted(Fixture f)
    {
        Require(!f.State.WorldConfiguration.HasPendingLayerNotifications && !f.OpenWorld().HasPendingLayerNotifications,
            "completed work was not durably acknowledged");
        var events = f.Ledger.ListAll();
        Require(events.Count == 3 && events.All(item => item.Type == ServerEventType.ServerNotification
            && item.Actor == new EventParty("userA", "colonyA", "pollution-snapshot")), "notifications missing, duplicated or misattributed");
        Require(events.Any(item => item.Target == new EventParty("owner", "abandoned")), "original nearby recipient was lost");
    }

    private sealed class Fixture
    {
        public readonly string Root, Database;
        public readonly bool Legacy;
        public readonly JsonSlot Json = new();
        private readonly BinarySlot binary = new();
        public readonly DistanceCalculator Distance = new();
        public ClashOfRimNetworkState State = null!;
        public SqliteAuthoritativeEventLedger Ledger = null!;
        private SnapshotSupportDeathTests.InterruptedLedger? interruption;
        private readonly FileColonySnapshotIndexStore files;
        public Fixture(string root, bool legacy, SaveSnapshotPackage source)
        {
            Root = root; Legacy = legacy; Database = Path.Combine(root, "world.sqlite");
            ServerDatabaseMigrator.Migrate(Database);
            files = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
            using var compressed = new MemoryStream(source.Payload);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            var save = XDocument.Load(gzip);
            var game = save.Root!.Element("game")!;
            var world = game.Element("world");
            if (world is null) { world = new XElement("world"); game.Add(world); }
            world.Element("grid")?.Remove();
            world.Add(new XElement("grid", new XElement("layers", new XElement("values", new XElement("li",
                new XElement("def", "Surface"), new XElement("tilePollution", Convert.ToBase64String([0, 0, 0, 192])))))));
            string path = Path.Combine(root, "pollution.rws"); save.Save(path);
            var package = SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity("userA", "colonyA", "pollution-snapshot"),
                Accepted, SnapshotPayloadEncoding.GzipRws);
            files.StoreLatest(package, package.Index, Accepted);
            Open();
            ColonyDeletionStageTests.SeedWorld(State.WorldConfiguration);
            State.WorldConfiguration.ConfirmWorldTileFloatLayer(Layer, [new(1, 0.2f)]);
        }
        public WorldConfigurationRegistry OpenWorld() => Legacy ? new WorldConfigurationRegistry(Json, binary, Extensions)
            : new WorldConfigurationRegistry(new SqliteKeyedJsonRecordStore(Database, "world-configuration"), null, binary, Extensions);
        public void Open()
        {
            Ledger = new SqliteAuthoritativeEventLedger(Database);
            var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, SnapshotSupportDeathTests.InterruptedLedger>();
            interruption = (SnapshotSupportDeathTests.InterruptedLedger)proxy; interruption.Inner = Ledger;
            State = new ClashOfRimNetworkState(ledger: proxy, snapshotStore: files, worldConfigurationRegistry: OpenWorld(),
                worldTileDistanceCalculator: Distance, plugins: new ServerPluginRegistry().WithBuiltInPlugins(
                    [BiotechServerCompatibility.Descriptor with { RequiredPackageIds = [] }]));
            State.Players.RecordLatestSnapshotReference("observer", "observer-colony", "observer-snapshot", Accepted);
        }
        public void Project() => State.WorldLayers.Project("userA", "colonyA", files.GetLatest("userA", "colonyA"), Accepted);
        public void Fail(string failure)
        {
            if (failure == "invalid-batch") { interruption!.BeforeBatch = () => throw new InvalidDataException("injected invalid notification batch"); return; }
            if (failure == "after-batch") { interruption!.AfterBatch = () => throw new IOException("injected after notification commit"); return; }
            if (failure is "configuration" or "ack")
            {
                if (Legacy) { Json.Failure = failure; return; }
                Sql(Database, "create trigger fail_layer before insert on server_keyed_json_records when new.item_key = 'pending-layer-notifications'"
                    + (failure == "ack" ? " and new.content_json = '[]'" : "") + " begin select raise(abort, 'injected world-layer failure'); end;");
            }
            else Sql(Database, "create trigger fail_layer before insert on events "
                + (failure == "events-second" ? "when (select count(*) from events) = 1 " : "")
                + "begin select raise(abort, 'injected world-layer failure'); end;");
        }
        public void ClearFailure() { Json.Failure = null; interruption!.BeforeBatch = interruption.AfterBatch = null; Sql(Database, "drop trigger if exists fail_layer;"); }
    }

    private sealed class JsonSlot : IJsonPersistenceSlot
    {
        public string? Value, Failure;
        public string? Read() => Value;
        public void Write(string json)
        {
            if (Failure == "configuration" || Failure == "ack" && JsonNode.Parse(json)!["PendingLayerNotifications"]!.AsArray().Count == 0)
                throw new IOException("injected world-layer failure");
            Value = json;
        }
    }
    private sealed class BinarySlot : IBinaryPersistenceSlot
    { private byte[]? value; public byte[]? ReadBinary(string key) => value; public void WriteBinary(string key, byte[]? bytes) => value = bytes; }
    private sealed class DistanceCalculator : IWorldTileDistanceCalculator
    {
        public int Value;
        public int? TryCalculateDistance(WorldTileGeometryDistanceSource? geometry, WorldTileRef fromTile, WorldTileRef toTile, int crossLayerOverheadDistanceTiles) => Value;
    }
    private static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static bool SamePayload(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left.Payload) == JsonSerializer.Serialize(right.Payload);
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or SqliteException or InvalidOperationException or InvalidDataException) { return; }
        throw new Exception("World notification: injected failure was not reached");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("World notification: " + message); }
    private static void Sql(string database, string sql)
    { using var connection = new SqliteConnection("Data Source=" + database); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void WithFixture(Func<string, SaveSnapshotPackage> fixture, bool legacy, Action<Fixture> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-layer-notification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new Fixture(root, legacy, fixture("source"))); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
