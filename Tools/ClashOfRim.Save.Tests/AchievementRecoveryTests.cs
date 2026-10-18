using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using Microsoft.Data.Sqlite;

internal static class AchievementRecoveryTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool reopen in new[] { false, true })
        foreach (string table in new[] { "server_achievement_metric_events", "server_achievement_events", "events" })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-achievement-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                ClashOfRimNetworkState Open() => new(ledger: new SqliteAuthoritativeEventLedger(database),
                    achievements: new AchievementRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.Achievements), null));
                var state = Open();
                Execute(database, $"create trigger fail_achievement before insert on {table} begin select raise(abort, 'injected failure'); end;");
                bool failed = false;
                try { Record(state); }
                catch (SqliteException) { failed = true; }
                Require(failed, "Achievement persistence failure was not injected.");
                if (table != "events") Require(state.Achievements.ListAggregates().Count == 0, "Uncommitted achievement leaked into cache.");
                Execute(database, "drop trigger fail_achievement;");
                if (reopen) state = Open();
                Record(state);
                Record(state);
                state = Open();
                Record(state);
                Require(state.Achievements.ListAggregates().Single(r => r.AchievementId == "trades_10").Value == 10,
                    "Interrupted unlock was lost or counted twice.");
                Require(state.Ledger.ListAll().Count == 1, "Unlock notification was lost or duplicated.");
                Require(Scalar(database, "select count(*) from server_achievement_metric_events;") == 1
                    && Scalar(database, "select json_extract(content_json, '$.Value') from server_achievement_metric_aggregates;") == 10,
                    "Trade metric was lost or counted twice after recovery.");
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        }
    }

    private static void Record(ClashOfRimNetworkState state)
    {
        state.AchievementAwards.RecordCumulative("user", "colony", "trade-event", "snapshot",
            new Dictionary<string, long> { [AchievementRegistry.MetricCompletedTradeCount] = 10 }, DateTimeOffset.UnixEpoch);
    }

    private static long Scalar(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
