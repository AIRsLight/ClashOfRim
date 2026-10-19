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
    async Task AdminGuards()
    {
        var state = ServerState; int audits = state.AdminControl.ListAudit().Count;
        foreach (string kind in new[] { "LockMaintenance", "PromoteAdmin", "Ban", "Kick", "DeletePlayerSave", "ResetOfflinePassword" })
        {
            await Invoke("B", "StartAdminAction", new { actionKind = kind, targetUserId = "rimtest-A", message = "unauthorized" }); await Idle("B");
        }
        Check(state.AdminControl.ListAudit().Count == audits && !state.AdminControl.MaintenanceLoginLocked && !state.AdminControl.IsBanned("rimtest-A"), "Unauthorized admin action changed server state.");
        Check((await Call("A", "clash.state")).Bool("isInActiveMultiplayerSession"), "Unauthorized admin action kicked owner.");
        foreach (string kind in new[] { "Kick", "Ban", "RevokeAdmin", "DeletePlayerSave" })
        {
            await Invoke("A", "StartAdminAction", new { actionKind = kind, targetUserId = "rimtest-A" }); await Idle("A");
        }
        Check(state.AdminControl.ListAudit().Count == audits && state.WorldConfiguration.IsAdministrator("rimtest-A"), "Self-target protection failed.");
    }
    async Task AdminRoles()
    {
        await Invoke("A", "StartAdminAction", new { actionKind = "PromoteAdmin", targetUserId = "rimtest-B" }); await Idle("A");
        try
        {
            await Wait("B", "clash.state", null, s => s.Bool("isAdministrator"));
            await Invoke("B", "StartRefreshAdminStatus"); await Idle("B");
            await Wait("B", "clash.admin", null, s => s.ValueKind == JsonValueKind.Object && s.Bool("isAdministrator"));
            Check(ServerState.WorldConfiguration.IsAdministrator("rimtest-B"), "Promotion was not authoritative.");
        }
        finally { await Invoke("A", "StartAdminAction", new { actionKind = "RevokeAdmin", targetUserId = "rimtest-B" }); await Idle("A"); }
        await Wait("B", "clash.state", null, s => !s.Bool("isAdministrator"));
        Check(!ServerState.WorldConfiguration.IsAdministrator("rimtest-B"), "Revocation was not authoritative.");
    }
    async Task AdminBroadcast()
    {
        const string message = "RimTest notice 中文 Français 1,25";
        await Invoke("A", "StartAdminAction", new { actionKind = "Broadcast", targetUserId = "rimtest-B", message, persistentNotification = true }); await Idle("A");
        await Invoke("B", "StartRefreshEventsForTest");
        await Wait("B", "clash.events", new { limit = 200 }, p => Items(p).Any(v => NotificationScenario.HasMessage(v, message)));
        Check(ServerState.AdminControl.ListAudit().Any(a => a.ActionKind == "Broadcast" && a.Message == message), "Broadcast audit missing.");
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
