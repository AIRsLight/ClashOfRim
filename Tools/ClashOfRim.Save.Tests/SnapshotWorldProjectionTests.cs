using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;

internal static class SnapshotWorldProjectionTests
{
    private static readonly WorldConfigurationExtensionService Extensions = new([new BiotechWorldPollutionExtensionProvider()]);
    private const string Layer = BiotechCompatibilityKeys.WorldPollution;

    public static void Run()
    {
        foreach (bool sites in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string failure in new[] { "json", "insert", "delete" })
            VerifyRecovery(sites, reopen, failure);
    }

    private static void VerifyRecovery(bool sites, bool reopen, string failure)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-world-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "world.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var json = new FailingJson();
            var binary = new SubstrateGuard();
            WorldConfigurationRegistry Open() => failure == "json"
                ? new WorldConfigurationRegistry(json, binary, Extensions)
                : new WorldConfigurationRegistry(new SqliteKeyedJsonRecordStore(database, "world-configuration"), null, binary, Extensions);
            var world = Open();
            ColonyDeletionStageTests.SeedWorld(world);
            world.ConfirmWorldTileFloatLayer(Layer, [new(1, 0.2f), new(2, 0.3f)]);
            WorldConfigurationDto before = world.Current!;
            string baseline = JsonSerializer.Serialize(before);
            byte[] substrate = binary.Value!.ToArray();
            int substrateWrites = binary.Writes;
            binary.RejectWrites = true;
            if (failure == "json") json.Fail = true;
            else Sql(database, $"create trigger fail_projection before {failure} on server_keyed_json_records "
                + $"when {(failure == "insert" ? "new" : "old")}.item_key = '{(sites ? "player-colony-sites" : "extensions")}' "
                + "begin select raise(abort, 'injected projection failure'); end;");

            bool failed = false;
            try { Apply(world, sites); }
            catch (Exception ex) when (ex is SqliteException || ex is IOException { Message: "injected projection failure" }) { failed = true; }
            Require(failed, "persistence failure was not reached");
            Require(ReferenceEquals(world.Current, before), "failed projection published its candidate cache");
            Require(JsonSerializer.Serialize(world.Current) == baseline && JsonSerializer.Serialize(Open().Current) == baseline,
                "failed projection changed durable or cached configuration");
            if (failure == "json") json.Fail = false;
            else Sql(database, "drop trigger fail_projection;");
            if (reopen) world = Open();
            var increases = Apply(world, sites);
            if (!sites)
            {
                Require(increases.Count == 2 && increases.Single(item => item.Tile == 1) is { PreviousValue: 0.2f, CurrentValue: 0.7f }
                    && increases.Single(item => item.Tile == 4) is { PreviousValue: 0, CurrentValue: 0.4f },
                    "retry lost the original increase or used a failed candidate as baseline");
            }
            foreach (var current in new[] { world, Open() })
            {
                var configuration = current.Current!;
                if (sites)
                    Require(configuration.PlayerColonySites.Count == 3
                        && configuration.PlayerColonySites.Single(item => item.UserId == "owner" && item.ColonyId == "abandoned").Tile == 8,
                        "site replacement was not persisted after retry");
                else Require(JsonSerializer.Serialize(configuration.PlayerColonySites) == JsonSerializer.Serialize(before.PlayerColonySites),
                    "float projection changed colony sites");
                Require(before.PlayerColonySites.Where(item => item.ColonyId != "abandoned")
                    .All(item => configuration.PlayerColonySites.Any(candidate => JsonSerializer.Serialize(candidate) == JsonSerializer.Serialize(item))),
                    "projection removed unrelated colony sites");
                var values = Extensions.ReadTileFloatLayer(configuration.Extensions, Layer).ToDictionary(item => item.Tile, item => item.Value);
                Require(values.Count == 3 && values[1] == 0.7f && values[2] == 0.3f && values[4] == 0.4f,
                    "retry lost projected or unchanged layer values");
                Require(current.TryGetWorldSubstrate(configuration.WorldConfigurationId, out var persistedSubstrate)
                    && persistedSubstrate!.SequenceEqual(substrate), "projection changed the world substrate");
            }
            Require(binary.Writes == substrateWrites, "projection rewrote unrelated substrate storage");
            string completed = JsonSerializer.Serialize(world.Current);
            Require(Apply(world, sites).Count == 0 && JsonSerializer.Serialize(world.Current) == completed,
                "successful projection was not idempotent");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static IReadOnlyList<WorldTileFloatLayerIncrease> Apply(WorldConfigurationRegistry world, bool sites)
    {
        if (!sites) return world.ConfirmWorldTileFloatLayer(Layer, [new(1, 0.7f), new(4, 0.4f)]);
        var incoming = Extensions.ReplaceTileFloatLayer(world.Current!.Extensions, Layer, [new(1, 0.7f), new(2, 0.3f), new(4, 0.4f)]);
        world.RegisterPlayerColonySites("owner", "abandoned", [new("owner", "abandoned", "8", "8", 8, "Updated site")], incoming);
        return [];
    }

    private static void Sql(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("World projection: " + message); }
    private sealed class FailingJson : IJsonPersistenceSlot
    {
        private string? value;
        public bool Fail;
        public string? Read() => value;
        public void Write(string json) { if (Fail) throw new IOException("injected projection failure"); value = json; }
    }
    private sealed class SubstrateGuard : IBinaryPersistenceSlot
    {
        public byte[]? Value;
        public int Writes;
        public bool RejectWrites;
        public byte[]? ReadBinary(string key) => Value;
        public void WriteBinary(string key, byte[]? bytes)
        {
            if (RejectWrites) throw new Exception("Projection unexpectedly rewrote substrate");
            Writes++;
            Value = bytes;
        }
    }
}
