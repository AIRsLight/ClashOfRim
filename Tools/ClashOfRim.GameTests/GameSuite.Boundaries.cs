using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    ClashOfRimNetworkState ServerState => server.Services.GetRequiredService<ClashOfRimNetworkState>();
    async Task UnicodeChat()
    {
        const string message = "RimTest 中文 Français Türkçe İı 日本語 한국어 😀 1,25 / 1.25";
        await Invoke("A", "StartSendChatMessage", new { rawText = message });
        await Wait("A", "clash.state", null, s => !s.Bool("chatInProgress"));
        await Invoke("B", "StartRefreshChatMessages");
        var received = await Wait("B", "clash.records", new { kind = "chat", limit = 200 }, p => Items(p).Any(v => v.GetProperty("data").OptionalString("text") == message));
        Check(Items(received).Count(v => v.GetProperty("data").OptionalString("text") == message) == 1, "Unicode message duplicated.");
        string snapshot = (await Call("A", "clash.state")).String("currentSnapshotId");
        await Invoke("A", "StartSendChatMessage", new { rawText = " \t " });
        await Wait("A", "clash.state", null, s => !s.Bool("chatInProgress"));
        await Invoke("B", "StartRefreshChatMessages");
        var after = await Call("B", "clash.records", new { kind = "chat", limit = 200 });
        Check(!Items(after).Any(v => string.IsNullOrWhiteSpace(v.GetProperty("data").OptionalString("text"))), "Blank chat entered the feed.");
        Check((await Call("A", "clash.state")).String("currentSnapshotId") == snapshot, "Chat mutated colony snapshot.");
    }
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
