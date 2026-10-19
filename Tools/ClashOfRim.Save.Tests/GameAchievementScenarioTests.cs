using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameAchievementScenarioTests
{
    internal static void Unlock(Func<string, SaveSnapshotPackage> factory) => UnlockAsync(factory("achievement-unit")).GetAwaiter().GetResult();
    internal static void Boards(Func<string, SaveSnapshotPackage> factory) => BoardsAsync(factory("leaderboard-unit")).GetAwaiter().GetResult();
    private static async Task UnlockAsync(SaveSnapshotPackage template)
    {
        foreach (string generation in new[] { "first", "rebuilt" })
        {
            using var f = await Fixture.Create(template, generation: generation);
            await f.Trade.Trades.Fulfill();
            Check(f.Registry.ListAggregates().All(a => a.ColonyId == "old"), "First shipment unexpectedly unlocked the ten-trade achievement.");
            await f.Scenario.ReachTradeThreshold();
            Check(f.Trade.Creates == 10 && f.Trade.Deliveries == 10 && f.Trade.Stock["A"]["Steel"] == 50 && f.Trade.Stock["B"]["WoodLog"] == 50,
                "Achievement scenario bypassed real trade commands or changed the original nine additional shipments.");
            Check(f.BoardReads == 2 && f.OwnReads == 2 && f.Refreshes == 2, "Achievement accepted a stale generation/empty observation or skipped refresh.");
            Check(f.Registry.ListAggregates().Single(a => a.UserId == "rimtest-A" && a.ColonyId == generation + "-A").Value == 10,
                "Achievement aggregation crossed colony generations.");
        }
        foreach (string fault in new[] { "below-threshold", "wrong-progress" })
        {
            using var f = await Fixture.Create(template, fault: fault);
            await f.Trade.Trades.Fulfill();
            await Reject(f.Scenario.ReachTradeThreshold, fault);
        }
        using (var f = await Fixture.Create(template, fault: "old-only"))
        {
            await f.Trade.Trades.Fulfill();
            await Interrupted(f.Scenario.ReachTradeThreshold);
            Check(f.BoardReads == 2, "Historical colony alone satisfied current leaderboard observation.");
        }
        using (var f = await Fixture.Create(template, tradeFault: "offer-exception"))
        {
            await Interrupted(f.Scenario.ReachTradeThreshold);
            Check(f.Trade.Deliveries == 0 && f.Refreshes == 0 && f.Trade.Runtime.Count("clash.trade.offer") == 1,
                "Failed delivery was retried or replaced by direct achievement insertion.");
        }
    }
    private static async Task BoardsAsync(SaveSnapshotPackage template)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                using var f = await Fixture.Create(template, ranked: true);
                string history = f.Scenario.CaptureHistory(), own = f.Scenario.CaptureHistory("rimtest-A");
                await f.Scenario.Refresh("A");
                var deleted = await f.Scenario.Refresh("B", "A", "old");
                Check(deleted.GetProperty("items").GetArrayLength() == 0 && f.Refreshes == 2,
                    "Refresh lost the explicit deleted-generation target or returned another colony's achievements.");
                Check(history == f.Scenario.CaptureHistory() && own == f.Scenario.CaptureHistory("rimtest-A"), "Read-only refresh changed history.");
                f.Record("unrelated", "home", "trades_10", 500);
                Check(history != f.Scenario.CaptureHistory() && own == f.Scenario.CaptureHistory("rimtest-A"), "History capture mixed account/global scope or cached stale rows.");
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var missed = new List<string>();
        foreach (string fault in new[] { "substitution", "duplicate-category", "source", "inactive", "value", "order", "duplicate-entry", "missing-entry", "excess-entry", "missing-category", "unknown-category", "deleted" })
        {
            using var f = await Fixture.Create(template, ranked: true, fault: fault);
            try { await f.Scenario.Refresh("A"); missed.Add(fault); }
            catch (AssertionException) { }
        }
        Check(missed.Count == 0, "Leaderboard assertion missed: " + string.Join(", ", missed));
        foreach (string fault in new[] { "refresh-response", "version-stuck" })
        {
            using var f = await Fixture.Create(template, ranked: true, fault: fault);
            await Interrupted(() => f.Scenario.Refresh("B"));
            Check(f.Refreshes == 1 && f.BoardReads == 0, "Failed/stale refresh retried mutation or read stale leaderboard data.");
        }
        using (var f = await Fixture.Create(template, ranked: true))
        {
            foreach (var snapshot in f.Snapshots.ListLatest()) f.Snapshots.RemoveLatest(snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!);
            f.VisibleBoards.Clear();
            await f.Scenario.Refresh("A");
            Check(f.Registry.ListAggregates().Count > 0, "Empty visibility erased achievement history.");
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly GameTradeTestFixture Trade;
        internal readonly AchievementRegistry Registry = new();
        internal readonly InMemoryColonySnapshotIndexStore Snapshots = new();
        internal readonly PlayerRegistry Players = new();
        internal readonly AchievementScenario Scenario;
        internal readonly JsonArray VisibleBoards = [];
        internal int Refreshes, BoardReads, OwnReads;
        private readonly Dictionary<string, int> versions = new() { ["A"] = 4, ["B"] = 7 }, pending = [];
        private readonly Dictionary<string, (string User, string Colony)> targets = [];
        private readonly string generation, fault;
        private readonly bool ranked;
        private readonly SaveSnapshotPackage template;
        private Fixture(GameTradeTestFixture trade, SaveSnapshotPackage template, string generation, string fault, bool ranked)
        {
            Trade = trade; this.template = template; this.generation = generation; this.fault = fault; this.ranked = ranked;
            Scenario = new(trade.Runtime.Run, trade.Runtime.Polling, trade.Trades, Registry, Snapshots, Players);
            SeedColony("rimtest-A", generation + "-A"); SeedColony("rimtest-B", generation + "-B");
            Record("rimtest-A", "old", "trades_10", 99); Players.MarkDeleted("rimtest-A", "old", "old-source", DateTimeOffset.UnixEpoch);
            if (ranked) SeedRanking();
            trade.Runtime.Script = Reply;
        }
        internal static async Task<Fixture> Create(SaveSnapshotPackage template, string generation = "rebuilt", string fault = "none", bool ranked = false, string tradeFault = "none")
            => new(await GameTradeTestFixture.Create(tradeFault), template, generation, fault, ranked);
        private void SeedColony(string user, string colony)
        {
            var identity = new SnapshotIdentity(user, colony, "source-" + user + "-" + colony);
            Snapshots.StoreLatest(new(identity, template.Envelope with { Identity = identity }, template.Index, DateTimeOffset.UnixEpoch));
            Players.Record(user, colony, identity.SnapshotId, DateTimeOffset.UnixEpoch);
        }
        internal void Record(string user, string colony, string id, long value)
            => Registry.Record(user, colony, "source-" + user + "-" + colony, new(id, "event-" + value, value), DateTimeOffset.UnixEpoch);
        private void SeedRanking()
        {
            // Fixed expected ordering includes Turkish I variants; do not derive the response by calling the scenario's ranking code.
            string[] users = ["rank-A", "rank-B", "rank-C", "rank-D", "rank-E", "rank-F", "rank-G", "rank-H", "rank-I", "rank-i", "rank-İ", "rank-ı"];
            foreach (string user in users) SeedColony(user, "home");
            foreach (string id in new[] { "trades_10", "wealth_500000" })
            {
                for (int i = 0; i < users.Length; i++) Record(users[i], "home", id, id == "trades_10" ? 1000 - i : 1L << 34);
                VisibleBoards.Add(Board(id, users.Take(10).Select(user => Row(user, "home", id)).ToArray()));
                Record("zz-inactive", "home", id, 1L << 34);
                SeedColony("zz-deleted", "home"); Record("zz-deleted", "home", id, 1L << 34);
                Players.MarkDeleted("zz-deleted", "home", "deleted-source", DateTimeOffset.UnixEpoch);
            }
        }
        private JsonNode Row(string user, string colony, string id)
        {
            var record = Registry.ListAggregates().Single(a => a.UserId == user && a.ColonyId == colony && a.AchievementId == id);
            return JsonSerializer.SerializeToNode(new { userId = user, colonyId = colony, value = record.Value, sourceSnapshotId = record.SourceSnapshotId })!;
        }
        private static JsonNode Board(string id, params JsonNode[] entries) => new JsonObject
        { ["data"] = new JsonObject { ["achievementId"] = id, ["entries"] = new JsonArray(entries) } };
        private object? Reply(string client, string method, JsonElement args)
        {
            if (method == "clash.state") return new { colonyId = generation + "-" + client, currentSnapshotId = "current-snapshot" };
            if (method == "clash.achievement.version")
            {
                if (pending.GetValueOrDefault(client) > 0)
                {
                    if (--pending[client] == 0)
                    {
                        if (fault == "version-stuck") throw new IOException("Version did not advance.");
                        versions[client]++;
                    }
                }
                return new { version = versions[client] };
            }
            if (method == "clash.invoke" && args.String("action") == "StartRefreshAchievements")
            {
                Refreshes++; pending[client] = 2;
                var input = args.GetProperty("arguments");
                targets[client] = (input.OptionalString("targetUserId") ?? "rimtest-" + client, input.OptionalString("targetColonyId") ?? generation + "-" + client);
                if (fault == "refresh-response") throw new IOException("Lost refresh response.");
                return new { };
            }
            if (method == "clash.records" && args.String("kind") is "achievements" or "leaderboards")
            {
                Check(pending.GetValueOrDefault(client) == 0 && targets.ContainsKey(client), "Achievement observation preceded a completed fresh refresh.");
                if (args.String("kind") == "achievements")
                {
                    OwnReads++; var target = targets[client];
                    if (!ranked && OwnReads == 1) return new { items = Array.Empty<object>() };
                    return new { items = Registry.ListAggregates().Where(a => a.UserId == target.User && a.ColonyId == target.Colony && !Players.IsDeleted(a.UserId, a.ColonyId))
                        .Select(a => new { data = new { achievementId = a.AchievementId, value = fault == "below-threshold" ? 9 : fault == "wrong-progress" ? 11 : a.Value } }).ToArray() };
                }
                BoardReads++;
                if (ranked) return new { items = MutatedBoards() };
                Check(client == "B", "Unlock observed the wrong leaderboard client.");
                if (fault == "old-only" && BoardReads > 1) throw new IOException("Current generation never appeared.");
                return new { items = new[] { Board("trades_10", Row("rimtest-A", BoardReads == 1 ? "old" : generation + "-A", "trades_10"), Row("rimtest-B", generation + "-B", "trades_10")) } };
            }
            var reply = Trade.Reply(client, method, args);
            if (method == "clash.invoke" && args.String("action") == "StartFulfillTradeOrderByDropPod")
                foreach (string party in new[] { "A", "B" }) Registry.RecordCumulativeMetricAchievements("rimtest-" + party, generation + "-" + party,
                    "trade-" + Trade.Deliveries, "trade-" + Trade.Deliveries, new Dictionary<string, long> { [AchievementRegistry.MetricCompletedTradeCount] = 1 }, null, DateTimeOffset.UnixEpoch);
            return reply;
        }
        private JsonArray MutatedBoards()
        {
            var boards = (JsonArray)VisibleBoards.DeepClone(); if (boards.Count == 0) return boards;
            var entries = (JsonArray)boards[0]!["data"]!["entries"]!;
            switch (fault)
            {
                case "substitution": entries[9] = Row("rank-İ", "home", "trades_10"); break;
                case "duplicate-category": boards[1] = boards[0]!.DeepClone(); break;
                case "source": entries[0]!["sourceSnapshotId"] = "wrong-source"; break;
                case "value": entries[0]!["value"] = 99999; break;
                case "order": var first = entries[0]!.DeepClone(); entries[0] = entries[1]!.DeepClone(); entries[1] = first; break;
                case "duplicate-entry": entries[1] = entries[0]!.DeepClone(); break;
                case "missing-entry": entries.RemoveAt(9); break;
                case "excess-entry": entries.Add(Row("rank-İ", "home", "trades_10")); break;
                case "missing-category": boards.RemoveAt(1); break;
                case "unknown-category": boards[0]!["data"]!["achievementId"] = "unknown"; break;
                case "inactive": boards[1]!["data"]!["entries"]![9] = Row("zz-inactive", "home", "wealth_500000"); break;
                case "deleted": boards[1]!["data"]!["entries"]![9] = Row("zz-deleted", "home", "wealth_500000"); break;
            }
            return boards;
        }
        public void Dispose() => Trade.Dispose();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string fault)
    { try { await action(); } catch (AssertionException) { return; } throw new InvalidOperationException("Expected achievement assertion: " + fault); }
    private static async Task Interrupted(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected achievement interruption."); }
}
