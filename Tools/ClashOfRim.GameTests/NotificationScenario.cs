using AIRsLight.ClashOfRim.Events;
using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class NotificationScenario(TestRun run, ScenarioPolling polling, ServerEntryScenario entry,
    EventScenarioFixture eventFixture, IAuthoritativeEventLedger ledger)
{
    internal static bool HasMessage(JsonElement entry, string message)
    {
        var data = entry.GetProperty("data");
        if (data.String("eventType") != "serverNotification") return false;
        using var payload = JsonDocument.Parse(data.String("payloadSummary"));
        return payload.RootElement.OptionalString("Message") == message;
    }
    internal async Task OfflineDelivery()
    {
        const string durable = "RimTest durable offline notice 中文 é İ", transient = "RimTest online-only offline notice 中文 é İ";
        await entry.LogoutWithSnapshot("B");
        foreach (var (message, persistent) in new[] { (durable, true), (transient, false) })
        {
            await run.InvokeClientAsync("A", "StartAdminAction", new { actionKind = "Broadcast", targetUserId = "rimtest-B", message, persistentNotification = persistent });
            await polling.Idle("A");
        }
        AuthoritativeEvent[] Notices(string message) => ledger.ListAll().Where(e => e.Target.UserId == "rimtest-B"
            && e.Payload is ServerNotificationEventPayload p && p.Message == message).ToArray();
        Check(Notices(durable).Length == 1 && Notices(durable)[0].Status == ServerEventStatus.PendingOfflineDelivery, "Persistent offline notice was not queued.");
        Check(Notices(transient).Length == 0, "Online-only notice entered the offline queue.");
        string id = Notices(durable)[0].EventId;
        await entry.LoginExisting(true); await eventFixture.Receive("B", id); await polling.Idle("B");
        await eventFixture.AwaitLedger(id, e => e.Status == ServerEventStatus.AppliedToSnapshot);
        await entry.LogoutWithSnapshot("B"); await entry.LoginExisting(true);
        await run.InvokeClientAsync("B", "StartRefreshEventsForTest"); await polling.Idle("B"); await polling.Step("B", 600);
        var events = Items(await run.CallAsync("B", "clash.events", new { limit = 200 }));
        Check(!events.Any(v => HasMessage(v, transient)), "Online-only notification replayed after login.");
        Check(Notices(durable).Length == 1 && Notices(durable)[0].Status == ServerEventStatus.AppliedToSnapshot && Notices(transient).Length == 0,
            "Notification persistence changed after reconnect.");
    }
}
