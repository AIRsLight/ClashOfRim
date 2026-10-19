using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class AchievementScenario(TestRun run, ScenarioPolling polling, TradeScenario trades,
    AchievementRegistry registry, IColonySnapshotIndexStore snapshots, PlayerRegistry players)
{
    internal async Task ReachTradeThreshold()
    {
        // The first fulfilled order was tested above. Reach the shipped threshold by
        // nine further real deliveries; never insert achievement rows directly.
        for (int i = 1; i < 10; i++) await trades.Fulfill();
        await RefreshClient("A");
        var own = await polling.Wait("A", "clash.records", new { kind = "achievements" }, p => Items(p).Any(v => v.GetProperty("data").String("achievementId") == "trades_10"));
        var achievement = Items(own).Single(v => v.GetProperty("data").String("achievementId") == "trades_10").GetProperty("data");
        Check(achievement.GetProperty("value").GetInt64() >= 10, "Trade achievement has wrong progress.");
        string colony = (await run.CallAsync("A", "clash.state")).String("colonyId");
        var records = registry.ListAggregates().Where(a => a.UserId == "rimtest-A" && a.ColonyId == colony && a.AchievementId == "trades_10").ToArray();
        Check(records.Length == 1 && records[0].Value == achievement.GetProperty("value").GetInt64(), "Achievement UI differs from authoritative aggregate.");
        await RefreshClient("B");
        var boards = await polling.Wait("B", "clash.records", new { kind = "leaderboards" }, p => Items(p).Any(v => v.GetProperty("data").String("achievementId") == "trades_10" &&
            v.GetProperty("data").GetProperty("entries").EnumerateArray().Any(e => e.OptionalString("userId") == "rimtest-A" && e.OptionalString("colonyId") == colony)));
        VerifyLeaderboards(boards);
    }
    internal async Task<JsonElement> Refresh(string client, string? targetClient = null, string? targetColony = null)
    {
        await RefreshClient(client, targetClient, targetColony);
        VerifyLeaderboards(await run.CallAsync(client, "clash.records", new { kind = "leaderboards" }));
        return await run.CallAsync(client, "clash.records", new { kind = "achievements" });
    }
    private void VerifyLeaderboards(JsonElement observation)
    {
        var active = snapshots.ListLatest().Select(s => (s.Identity.OwnerId, s.Identity.ColonyId)).ToHashSet();
        var expected = registry.ListAggregates().Where(a => active.Contains((a.UserId, a.ColonyId)) && !players.IsDeleted(a.UserId, a.ColonyId))
            .GroupBy(a => a.AchievementId, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.OrderByDescending(a => a.Value).ThenBy(a => a.UserId, StringComparer.Ordinal).ThenBy(a => a.ColonyId, StringComparer.Ordinal).Take(10).ToArray(), StringComparer.Ordinal);
        var boards = Items(observation).Select(v => v.GetProperty("data")).ToArray();
        Check(boards.Length == expected.Count && boards.Select(b => b.String("achievementId")).ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys),
            "Leaderboard omitted, duplicated or introduced an achievement category.");
        foreach (var board in boards)
        {
            string id = board.String("achievementId");
            var actual = board.GetProperty("entries").EnumerateArray().Select(e =>
                (e.String("userId"), e.String("colonyId"), e.GetProperty("value").GetInt64(), e.OptionalString("sourceSnapshotId")));
            Check(actual.SequenceEqual(expected[id].Select(a => (a.UserId, a.ColonyId, a.Value, a.SourceSnapshotId))),
                "Leaderboard differs from authoritative top ten identities, order, values or source snapshots: " + id);
        }
    }
    private async Task RefreshClient(string client, string? targetClient = null, string? targetColony = null)
    {
        int version = (await run.CallAsync(client, "clash.achievement.version")).Int("version");
        await run.InvokeClientAsync(client, "StartRefreshAchievements", new { targetUserId = targetClient is null ? null : "rimtest-" + targetClient, targetColonyId = targetColony });
        await polling.Wait(client, "clash.achievement.version", null, v => v.Int("version") > version);
    }
    internal string CaptureHistory(string? userId = null) => JsonSerializer.Serialize(registry.ListAggregates().Where(a => userId is null || a.UserId == userId));
}
