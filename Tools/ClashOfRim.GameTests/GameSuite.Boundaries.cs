using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    ClashOfRimNetworkState ServerState => server.Services.GetRequiredService<ClashOfRimNetworkState>();
    async Task Achievements()
    {
        // The first fulfilled order was tested above. Reach the shipped threshold by
        // nine further real deliveries; never insert achievement rows directly.
        for (int i = 1; i < 10; i++) await trades.Fulfill();
        await Invoke("A", "StartRefreshAchievements");
        var own = await Wait("A", "clash.records", new { kind = "achievements" }, p => Items(p).Any(v => v.GetProperty("data").String("achievementId") == "trades_10"));
        var achievement = Items(own).Single(v => v.GetProperty("data").String("achievementId") == "trades_10").GetProperty("data");
        Check(achievement.Int("value") >= 10, "Trade achievement has wrong progress.");
        string colony = (await Call("A", "clash.state")).String("colonyId");
        var records = ServerState.Achievements.ListAggregates().Where(a => a.UserId == "rimtest-A" && a.ColonyId == colony && a.AchievementId == "trades_10").ToArray();
        Check(records.Length == 1 && records[0].Value == achievement.Int("value"), "Achievement UI differs from authoritative aggregate.");
        await Invoke("B", "StartRefreshAchievements");
        await Wait("B", "clash.records", new { kind = "leaderboards" }, p => Items(p).Any(v => v.GetProperty("data").String("achievementId") == "trades_10" &&
            v.GetProperty("data").GetProperty("entries").EnumerateArray().Any(e => e.OptionalString("userId") == "rimtest-A")));
    }
}
