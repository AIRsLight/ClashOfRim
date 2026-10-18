using System.Globalization;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class AchievementLifecycleTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        string root = Path.Combine(Path.GetTempPath(), "cor-achievement-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            string database = Path.Combine(root, "server.sqlite"); ServerDatabaseMigrator.Migrate(database);
            AchievementRegistry Open() => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.Achievements), null);
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var registry = Open(); string colony = "threshold-" + culture;
                foreach (var definition in registry.ListDefinitions())
                {
                    void Record(long value, string snapshot) => registry.RecordSnapshotMetricAchievements("threshold-user", colony, snapshot,
                        new Dictionary<string, long> { [definition.MetricId] = value }, null, DateTimeOffset.UnixEpoch);
                    Record(definition.Threshold - 1, "below");
                    Require(!registry.ListAggregates().Any(a => a.ColonyId == colony && a.AchievementId == definition.AchievementId), "Below-threshold unlock: " + definition.AchievementId);
                    Record(definition.Threshold, "exact"); Record(definition.Threshold, "replay");
                    Record(definition.Threshold + 2, "high"); Record(definition.Threshold, "lower");
                    registry = Open();
                    var aggregate = registry.ListAggregates().Single(a => a.ColonyId == colony && a.AchievementId == definition.AchievementId);
                    Require(aggregate.Value == definition.Threshold + 2 && aggregate.SourceSnapshotId == "high", "Max achievement lost its peak or source after reopen: " + definition.AchievementId);
                }
                // Real cumulative algorithm: repeated source IDs, zero/negative values,
                // a later valid event and a fresh colony generation under the same user.
                void Trade(string generation, string source, long delta) => registry.RecordCumulativeMetricAchievements("trade-user", generation, source, source,
                    new Dictionary<string, long> { [AchievementRegistry.MetricCompletedTradeCount] = delta }, null, DateTimeOffset.UnixEpoch);
                Trade(colony, "first-nine", 9); Trade(colony, "first-nine", 9); Trade(colony, "negative", -1); Trade(colony, "zero", 0);
                Require(!registry.ListAggregates().Any(a => a.UserId == "trade-user" && a.ColonyId == colony), "Invalid/repeated delta unlocked cumulative achievement.");
                Trade(colony, "tenth", 1); registry = Open(); Trade(colony, "tenth", 1);
                Trade(colony + "-new", "tenth", 1);
                Require(registry.ListAggregates().Single(a => a.UserId == "trade-user" && a.ColonyId == colony).Value == 10
                    && !registry.ListAggregates().Any(a => a.UserId == "trade-user" && a.ColonyId == colony + "-new"), "Cumulative metrics crossed a generation/replay boundary.");
            }
            var snapshots = new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots"));
            var players = new PlayerRegistry(Path.Combine(root, "players.json"));
            var achievements = Open();
            for (int i = 0; i < 12; i++)
            {
                string user = "rank-" + i.ToString("D2", CultureInfo.InvariantCulture), colony = "colony-" + i;
                var package = fixture("ranking-" + i);
                package = package with { Envelope = package.Envelope with { Identity = package.Envelope.Identity with { OwnerId = user, ColonyId = colony } } };
                snapshots.StoreLatest(package, package.Index, DateTimeOffset.UnixEpoch);
                players.Record(user, colony, package.Envelope.Identity.SnapshotId, DateTimeOffset.UnixEpoch, "名称 " + i);
                achievements.RecordSnapshotMetricAchievements(user, colony, package.Envelope.Identity.SnapshotId!,
                    new Dictionary<string, long> { [AchievementRegistry.MetricColonyWealth] = 500000 + i / 2 }, null, DateTimeOffset.UnixEpoch);
            }
            ClashOfRimNetworkState State() => new(snapshotStore: snapshots, playerRegistry: new PlayerRegistry(Path.Combine(root, "players.json")), achievements: Open());
            var state = State(); var response = List(state, "rank-00", "colony-0");
            var board = response.Leaderboards.Single(b => b.AchievementId == "wealth_500000");
            string[] expected = Enumerable.Range(0, 12).OrderByDescending(i => i / 2).ThenBy(i => i).Take(10).Select(i => "rank-" + i.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
            Require(board.Entries.Select(e => e.UserId).SequenceEqual(expected), "Leaderboard top ten/tie ordering changed.");
            Require(response.OwnAchievements.Single().Value == 500000 && board.Entries.All(e => e.DisplayName?.StartsWith("名称 ", StringComparison.Ordinal) == true), "Own value/display name mapping changed.");
            snapshots.RemoveLatest("rank-11", "colony-11"); players.MarkDeleted("rank-11", "colony-11", "ranking-11", DateTimeOffset.UnixEpoch);
            state = State(); response = List(state, "rank-11", "colony-11");
            Require(response.OwnAchievements.Count == 0 && response.Leaderboards.All(b => b.Entries.All(e => e.UserId != "rank-11")), "Deleted colony visible after persistence reopen/stale query.");
            Require(state.Players.IsDeleted("rank-11", "colony-11") && state.Achievements.ListAggregates().Any(a => a.UserId == "rank-11"), "Stale query resurrected colony or erased history.");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true);
        }
    }
    private static ListAchievementsResponse List(ClashOfRimNetworkState state, string user, string colony)
    {
        return state.AchievementQueries.List(new("rank-00", "colony-0", "ranking-0", user, colony));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
