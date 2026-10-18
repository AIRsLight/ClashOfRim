using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class ColonyDeletionStageTests
{
    public static void Run()
    {
        foreach (bool reopen in new[] { false, true })
        {
            foreach (string phase in new[] { "json", "insert", "delete" }) VerifyWorldFailure(phase, reopen);
            foreach (string target in new[] { "memo-other", "outgoing-raid", "incoming-gift" }) VerifyEventFailure(target, reopen);
        }
        foreach (string kind in new[] { "memory", "file", "sqlite" }) VerifyEventSemantics(kind);
    }

    private static void VerifyWorldFailure(string phase, bool reopen)
    {
        WithDirectory(root =>
        {
            string database = Path.Combine(root, "world.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var json = new FailingJson();
            var binary = new GuardedBinary();
            WorldConfigurationRegistry Open() => phase == "json"
                ? new WorldConfigurationRegistry(json, binary)
                : new WorldConfigurationRegistry(new SqliteKeyedJsonRecordStore(database, "world-configuration"), null, binary, null);
            var world = Open();
            SeedWorld(world);
            var before = world.Current;
            binary.FailWrites = true; // Removing sites must not rewrite the unchanged substrate.
            if (phase == "json") json.Fail = true;
            else Execute(database, $"create trigger fail_world before {phase} on server_keyed_json_records "
                + $"when {(phase == "insert" ? "new" : "old")}.item_key = 'player-colony-sites' "
                + "begin select raise(abort, 'injected site deletion failure'); end;");
            bool failed = false;
            try { world.RemovePlayerColonySites("owner", "abandoned"); }
            catch (Exception ex) when (ex is SqliteException || ex is IOException { Message: "injected JSON failure" }) { failed = true; }
            Require(failed && ReferenceEquals(before, world.Current), "failed world removal published candidate cache");
            Require(world.Current!.PlayerColonySites.Count == 4 && Open().Current!.PlayerColonySites.Count == 4,
                "failed site deletion changed cache or persisted sites");
            if (phase == "json") json.Fail = false;
            else Execute(database, "drop trigger fail_world;");
            if (reopen) world = Open();
            Require(world.RemovePlayerColonySites("owner", "abandoned").RemovedCount == 2, "retry skipped sites lost from failed cache");
            foreach (var current in new[] { world, Open() })
            {
                Require(current.Current!.PlayerColonySites.Count == 2
                    && current.Current.PlayerColonySites.All(site => site.ColonyId != "abandoned"), "site removal changed unrelated colonies or was not durable");
                Require(current.TryGetWorldSubstrate("world-stage-test", out var substrate)
                    && substrate!.SequenceEqual(binary.Value!), "site removal changed substrate bytes");
            }
            Require(world.RemovePlayerColonySites("owner", "abandoned").RemovedCount == 0, "repeat site deletion was not a no-op");
        });
    }

    internal static void SeedWorld(WorldConfigurationRegistry world, string user = "owner", string colony = "abandoned")
    {
        world.Prepare("admin");
        world.Submit("admin", new WorldConfigurationDto("world-stage-test", "admin", "admin-colony", DateTimeOffset.UnixEpoch,
            "seed", "0.3", "Normal", "Normal", "Normal", "Normal", "10", ["OutlanderCivil"], [], [], [], [],
            [new(user, colony, "1", "1", 1, "first"), new(user, colony, "2", "2", 2, "second"),
             new("owner", "other-colony", "3", "3", 3, "same user"), new("other-user", "other", "4", "4", 4, "other user")]));
        byte[] geometry = WorldTileGeometryBinaryCodec.Encode(new WorldTileGeometryDto(
            [new WorldTileLayerGeometryDto(0, "Surface", 1, [new WorldTileCenterDto(0, 1, 0, 0)])]))!;
        byte[] payload = WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(1,
            "<grid><layers><keys><li>0</li></keys><values><li /></values></layers></grid>", "<features />", "<landmarks />", geometry));
        Require(world.StoreWorldSubstrate("admin", "admin-colony", "world-stage-test", payload).Accepted && world.Current is not null,
            "world fixture is not ready");
    }

    private static void VerifyEventFailure(string target, bool reopen)
    {
        WithDirectory(root =>
        {
            string database = Path.Combine(root, "events.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database));
            SeedEvents(state.Ledger);
            var before = state.Ledger.ListAll().ToDictionary(item => item.EventId, item => item.Status);
            Execute(database, $"create trigger fail_close before update on events when old.event_id = '{target}' "
                + "begin select raise(abort, 'injected lifecycle closure failure'); end;");
            bool failed = false;
            try { CloseEvents(state); }
            catch (TargetInvocationException ex) when (ex.InnerException is SqliteException) { failed = true; }
            Require(failed, "event closure fault was not reached");
            foreach (var ledger in new[] { state.Ledger, new SqliteAuthoritativeEventLedger(database) })
                Require(before.All(pair => ledger.Find(pair.Key)!.Status == pair.Value) && ledger.ListAll().Count == before.Count,
                    "abandonment committed only part of its event closure graph");
            Execute(database, "drop trigger fail_close;");
            if (reopen) state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database));
            CloseEvents(state);
            VerifyClosed(state.Ledger);
            VerifyClosed(new SqliteAuthoritativeEventLedger(database));
            CloseEvents(state);
            VerifyClosed(state.Ledger);
        });
    }

    private static void VerifyEventSemantics(string kind)
    {
        WithDirectory(root =>
        {
            string path = Path.Combine(root, "ledger");
            IAuthoritativeEventLedger Open() => kind switch
            {
                "file" => new FileAuthoritativeEventLedger(path),
                "sqlite" => new SqliteAuthoritativeEventLedger(path),
                _ => new InMemoryAuthoritativeEventLedger()
            };
            var state = new ClashOfRimNetworkState(ledger: Open());
            SeedEvents(state.Ledger);
            CloseEvents(state);
            VerifyClosed(state.Ledger);
            CloseEvents(state);
            VerifyClosed(kind == "memory" ? state.Ledger : Open());
        });
    }

    private static void SeedEvents(IAuthoritativeEventLedger ledger)
    {
        var abandoned = new EventParty("owner", "abandoned");
        var other = new EventParty("other", "other-colony");
        void Add(string id, ServerEventType type, EventParty actor, EventParty target, LedgerEventPayload payload,
            ServerEventStatus status = ServerEventStatus.Recorded) => ledger.Append(
                AuthoritativeEventFactory.Create(type, actor, target, id, false, payload, DateTimeOffset.UnixEpoch)
                    with { EventId = id, Status = status });
        TradeEventPayload Trade(string id, TradeStage stage) => new(id, stage, [], [], 0);
        Add("order", ServerEventType.Trade, abandoned, new EventParty("server"), Trade("order", TradeStage.MarketOrder));
        Add("memo-other", ServerEventType.Trade, other, other, Trade("order", TradeStage.AcceptedMemo));
        Add("memo-overlap", ServerEventType.Trade, abandoned, abandoned, Trade("order", TradeStage.AcceptedMemo));
        Add("memo-applied", ServerEventType.Trade, other, other, Trade("order", TradeStage.AcceptedMemo), ServerEventStatus.AppliedToSnapshot);
        Add("other-order", ServerEventType.Trade, other, new EventParty("server"), Trade("other-order", TradeStage.MarketOrder));
        Add("own-memo", ServerEventType.Trade, abandoned, abandoned, Trade("other-order", TradeStage.AcceptedMemo));
        var raid = new RaidEventPayload("defender-snapshot", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), null);
        Add("outgoing-raid", ServerEventType.Raid, abandoned, other, raid);
        Add("incoming-raid", ServerEventType.Raid, other, abandoned, raid);
        var notification = new ServerNotificationEventPayload("notice", "title", "message", ServerNotificationSeverity.Info, false);
        Add("incoming-gift", ServerEventType.ServerNotification, other, abandoned, notification);
        Add("legacy-user", ServerEventType.ServerNotification, other, new EventParty("owner"), notification);
        Add("same-user-new-colony", ServerEventType.ServerNotification, other, new EventParty("owner", "new-colony"), notification);
        Add("already-applied", ServerEventType.ServerNotification, other, abandoned, notification, ServerEventStatus.AppliedToSnapshot);
    }

    private static void VerifyClosed(IAuthoritativeEventLedger ledger)
    {
        Require(ledger.ListAll().Count == 12, "closure appended or deleted ledger records");
        foreach (var item in ledger.ListAll())
        {
            ServerEventStatus expected = item.EventId switch
            {
                "outgoing-raid" => ServerEventStatus.Failed,
                "other-order" or "same-user-new-colony" => ServerEventStatus.Recorded,
                "already-applied" or "memo-applied" => ServerEventStatus.AppliedToSnapshot,
                _ => ServerEventStatus.Cancelled
            };
            Require(item.Status == expected, "unexpected closure status: " + item.EventId);
        }
    }

    private static void CloseEvents(ClashOfRimNetworkState state)
    {
        object abandoned = AIRsLight.ClashOfRim.Network.Modules.Sessions.ClientLifecycleEvent.ColonyAbandoned(
            "owner", "abandoned", "snapshot", DateTimeOffset.UnixEpoch);
        typeof(ClashOfRimNetworkServer).GetMethod("ReconcileAbandonedColonyOnClientLifecycle", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new[] { state, abandoned });
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-deletion-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Deletion stage: " + message);
    }

    private sealed class FailingJson : IJsonPersistenceSlot
    {
        private string? value;
        public bool Fail;
        public string? Read() => value;
        public void Write(string json)
        {
            if (Fail) throw new IOException("injected JSON failure");
            value = json;
        }
    }

    private sealed class GuardedBinary : IBinaryPersistenceSlot
    {
        public byte[]? Value;
        public bool FailWrites;
        public byte[]? ReadBinary(string key) => Value;
        public void WriteBinary(string key, byte[]? bytes)
        {
            if (FailWrites) throw new IOException("unexpected substrate rewrite");
            Value = bytes;
        }
    }
}
