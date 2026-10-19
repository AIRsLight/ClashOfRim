using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ChatScenario(TestRun run, ScenarioPolling polling)
{
    private const string PrivateMessage = "RimTest private exchange";
    internal async Task Public()
    {
        const string message = "RimTest public exchange";
        await Send("A", message);
        var received = Items(await Receive("B", message)).Single(v => HasText(v, message)).GetProperty("data");
        Check(received.String("channel") == "Public" && received.OptionalString("targetUserId") is null, "Public chat routing changed.");
    }
    internal async Task Private()
    {
        await Send("A", "@rimtest-B " + PrivateMessage);
        var messages = await Receive("B", PrivateMessage);
        var message = Items(messages).Single(v => HasText(v, PrivateMessage)).GetProperty("data");
        Check(message.String("channel") == "Private" && message.String("targetUserId") == "rimtest-B", "Private chat routing changed.");
        Check((await run.CallAsync("B", "clash.state")).Int("unreadPrivateChatCount") > 0, "Incoming private chat was not marked unread.");
        await run.InvokeClientAsync("B", "MarkPrivateChatRead");
        Check((await run.CallAsync("B", "clash.state")).Int("unreadPrivateChatCount") == 0, "Private chat read marker was not cleared.");
    }
    internal async Task UnicodeAndBlankRejection()
    {
        const string message = "RimTest 中文 Français Türkçe İı 日本語 한국어 😀 1,25 / 1.25";
        await Send("A", message);
        var received = await Receive("B", message, 200);
        Check(Items(received).Count(v => HasText(v, message)) == 1, "Unicode message duplicated.");
        string snapshot = (await run.CallAsync("A", "clash.state")).String("currentSnapshotId");
        await Send("A", " \t ");
        await run.InvokeClientAsync("B", "StartRefreshChatMessages");
        var after = await run.CallAsync("B", "clash.records", new { kind = "chat", limit = 200 });
        Check(!Items(after).Any(v => string.IsNullOrWhiteSpace(v.GetProperty("data").OptionalString("text"))), "Blank chat entered the feed.");
        Check((await run.CallAsync("A", "clash.state")).String("currentSnapshotId") == snapshot, "Chat mutated colony snapshot.");
    }
    internal Task<JsonElement> VerifyPrivateHistory(string client) => Receive(client, PrivateMessage);
    internal static bool HasText(JsonElement entry, string text) => entry.GetProperty("data").OptionalString("text") == text;
    private async Task Send(string client, string rawText)
    {
        await run.InvokeClientAsync(client, "StartSendChatMessage", new { rawText });
        await polling.Wait(client, "clash.state", null, s => !s.Bool("chatInProgress"));
    }
    private async Task<JsonElement> Receive(string client, string text, int? limit = null)
    {
        await run.InvokeClientAsync(client, "StartRefreshChatMessages");
        return await polling.Wait(client, "clash.records", limit is int count ? new { kind = "chat", limit = count } : new { kind = "chat" },
            p => Items(p).Any(v => HasText(v, text)));
    }
}
