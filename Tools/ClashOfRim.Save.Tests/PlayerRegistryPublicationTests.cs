using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using Microsoft.Data.Sqlite;

internal static class PlayerRegistryPublicationTests
{
    internal static void Run()
    {
        int count = 0;
        foreach (string store in new[] { "memory", "file", "sqlite-document", "sqlite-structured" })
            foreach (string operation in new[] { "record", "reference", "wealth", "remove", "delete", "delete-old" })
                foreach (bool reopen in new[] { false, true })
                { Publication(store, operation, reopen); count++; }
        LegacySourceIsReadOnly(); count++;
        Console.WriteLine($"PASS: player registry publication ({count} scenarios)");
    }

    private static void Publication(string store, string operation, bool reopen) => RecoveryTestSupport.WithDatabase((root, db) =>
    {
        var slot = new RecoveryTestSupport.Slot(store switch
        {
            "file" => new FileJsonPersistenceSlot(Path.Combine(root, "players.json")),
            "sqlite-document" => new SqliteJsonPersistenceSlot(db, "players-test"),
            _ => null
        });
        bool structured = store == "sqlite-structured";
        PlayerRegistry Open() => structured ? new(new SqlitePlayerRegistryStore(db), null) : new(slot);
        var registry = Open();
        registry.Record("user", "current", "initial", DateTimeOffset.UnixEpoch, "名称");
        registry.Record("other", "separate", "separate", DateTimeOffset.UnixEpoch);
        registry.MarkDeleted("other", "old", "old-snapshot", DateTimeOffset.UnixEpoch);
        string Before(PlayerRegistry value) => JsonSerializer.Serialize(new { Players = value.List(),
            DeletedCurrent = value.IsDeleted("user", "current"), DeletedOld = value.IsDeleted("user", "old"), Existing = value.IsDeleted("other", "old") });
        string before = Before(registry);
        void Sql(string sql) { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        if (structured)
        {
            string action = operation == "remove" ? "delete" : "insert";
            string table = operation.StartsWith("delete", StringComparison.Ordinal) ? "server_player_colony_tombstones" : "server_players";
            Sql($"create trigger fail_players before {action} on {table} begin select raise(abort, 'player publication failure'); end;");
        }
        else slot.Fail = true;
        bool failed = false;
        try { Change(registry, operation); } catch (Exception ex) when (ex is IOException or SqliteException) { failed = true; }
        Require(failed && before == Before(registry) && before == Before(Open()), "Failed player publication changed memory/disk: " + store + "/" + operation);
        if (structured) Sql("drop trigger fail_players;"); else slot.Fail = false;
        if (reopen) registry = Open();
        Change(registry, operation); var persisted = Open();
        Require(Before(registry) == Before(persisted) && persisted.FindByUserId("other")?.ColonyId == "separate" && persisted.IsDeleted("other", "old"),
            "Player retry diverged or damaged unrelated records.");
        var player = persisted.FindByUserId("user");
        switch (operation)
        {
            case "record": Require(player?.CurrentSnapshotId == "recorded" && player.DisplayName == "new-name", "Activity retry lost state."); break;
            case "reference": Require(player?.CurrentSnapshotId == "reference", "Snapshot reference retry lost state."); break;
            case "wealth": Require(player?.LatestSnapshotWealth == 77 && player.LatestSnapshotWealthSnapshotId == "wealth", "Wealth retry lost state."); break;
            case "remove": Require(player is null && !persisted.IsDeleted("user", "current"), "Plain removal changed tombstone semantics."); break;
            case "delete": Require(player is null && persisted.IsDeleted("user", "current"), "Deletion did not atomically remove and tombstone."); break;
            case "delete-old": Require(player?.ColonyId == "current" && persisted.IsDeleted("user", "old"), "Old-colony tombstone removed a rebuilt account."); break;
        }
        if (operation == "delete")
        {
            persisted.Record("user", "current", "stale", DateTimeOffset.UtcNow);
            persisted.RecordLatestSnapshotReference("user", "current", "stale", DateTimeOffset.UtcNow);
            persisted.RecordLatestSnapshotWealth("user", "current", "stale", 999, DateTimeOffset.UtcNow);
            Require(persisted.FindByUserId("user") is null && Open().FindByUserId("user") is null, "Deleted colony was resurrected by a shared publication entry.");
        }
    });

    private static void Change(PlayerRegistry players, string operation)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        switch (operation)
        {
            case "record": players.Record("user", "current", "recorded", now, "new-name"); break;
            case "reference": players.RecordLatestSnapshotReference("user", "current", "reference", now); break;
            case "wealth": players.RecordLatestSnapshotWealth("user", "current", "wealth", 77, now); break;
            case "remove": players.Remove("user"); break;
            case "delete": players.MarkDeleted("user", "current", "last", now); break;
            case "delete-old": players.MarkDeleted("user", "old", "last", now); break;
        }
    }

    private static void LegacySourceIsReadOnly() => RecoveryTestSupport.WithDatabase((_, db) =>
    {
        var legacy = new RecoveryTestSupport.Slot { Fail = true };
        var players = new PlayerRegistry(new SqlitePlayerRegistryStore(db), legacy);
        players.Record("user", "current", "base", DateTimeOffset.UnixEpoch);
        Require(new PlayerRegistry(new SqlitePlayerRegistryStore(db), legacy).FindByUserId("user")?.CurrentSnapshotId == "base" && legacy.Writes == 0,
            "Structured primary store also wrote an obsolete legacy import source.");
    });

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
