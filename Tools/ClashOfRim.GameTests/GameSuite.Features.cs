using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RimTest.Client;
using RimTest.Protocol;
using RimTest.Testing;


namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task PrivateChat()
    {
        await Invoke("A", "StartSendChatMessage", new { rawText = "@rimtest-B RimTest private exchange" });
        await Wait("A", "clash.state", null, s => !s.Bool("chatInProgress"));
        await Invoke("B", "StartRefreshChatMessages");
        var messages = await Wait("B", "clash.records", new { kind = "chat" }, p => Items(p).Any(v => v.GetProperty("data").GetRawText().Contains("RimTest private exchange")));
        var message = Items(messages).Single(v => v.GetProperty("data").GetRawText().Contains("RimTest private exchange")).GetProperty("data");
        Check(message.String("channel") == "Private" && message.String("targetUserId") == "rimtest-B", "Private chat routing changed.");
        Check((await Call("B", "clash.state")).Int("unreadPrivateChatCount") > 0, "Incoming private chat was not marked unread.");
        await Invoke("B", "MarkPrivateChatRead");
        Check((await Call("B", "clash.state")).Int("unreadPrivateChatCount") == 0, "Private chat read marker was not cleared.");
    }
    async Task Appearance()
    {
        var state = server.Services.GetRequiredService<ClashOfRimNetworkState>();
        var original = state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance;
        await Invoke("A", "SaveColonyAppearance", new { mode = "Color", iconDefName = (string?)null, colorDefName = (string?)null, colorHex = "38A4E8" });
        var watch = Stopwatch.StartNew();
        while (state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex != "38A4E8" && watch.Elapsed.TotalSeconds < 90)
            await Task.Delay(100);
        Check(state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex == "38A4E8", "Appearance was not registered on server.");
        await Invoke("B", "StartSyncWorldMapMarkers");
        await Wait("B", "clash.records", new { kind = "markers" }, p => Items(p).Any(v =>
            v.GetProperty("data").OptionalString("ownerUserId") == "rimtest-A" &&
            v.GetProperty("data").GetProperty("appearance").OptionalString("colorHex") == "38A4E8"));
        await Invoke("A", "SaveColonyAppearance", new { mode = original?.Mode, iconDefName = original?.IconDefName, colorDefName = original?.ColorDefName, colorHex = original?.ColorHex });
        watch.Restart();
        while (state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex != original?.ColorHex && watch.Elapsed.TotalSeconds < 90)
            await Task.Delay(100);
        Check(state.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A").Appearance?.ColorHex == original?.ColorHex, "Appearance restore failed.");
    }
    async Task Alliance()
    {
        await Idle("A"); await Invoke("A", "StartRefreshPlayers"); await Idle("A");
        var players = await Wait("A", "clash.records", new { kind = "players" }, p => Items(p).Any(v => v.GetProperty("data").String("userId") == "rimtest-B"));
        var target = Items(players).Single(v => v.GetProperty("data").String("userId") == "rimtest-B");
        await Invoke("A", "StartCreateDiplomacyEvent", new { kind = "AllianceRequest", target = target.GetProperty("reference"), customMessage = "RimTest alliance" });
        await Idle("A");
        var events = await Wait("B", "clash.events", new { limit = 200 }, p => Items(p).Any(v => v.GetProperty("data").String("payloadSummary").Contains("AllianceRequest")));
        var request = Items(events).Single(v => v.GetProperty("data").String("payloadSummary").Contains("AllianceRequest"));
        await Invoke("B", "HandleEventLetterAction", new { eventId = request.GetProperty("data").String("eventId"), actionKind = "Accept" });
        await Idle("B");
        await Invoke("A", "StartRefreshPlayers"); await Idle("A");
        await Wait("A", "clash.records", new { kind = "players" }, p => Items(p).Any(v => v.GetProperty("data").String("userId") == "rimtest-B" && v.GetProperty("data").OptionalString("relationKind") == "Ally"));
    }
}
