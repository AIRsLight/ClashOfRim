using System.Diagnostics;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task LogoutWithSnapshot(string client)
    {
        await Idle(client); await Invoke(client, "StartManualSnapshotUpload"); await Idle(client);
        await entry.ExitSession(client);
        // The menu becomes ready before the asynchronous logout reaches the server.
        var timer = Stopwatch.StartNew();
        while (ServerState.OnlinePresence.IsUserOnline("rimtest-" + client) && timer.Elapsed.TotalSeconds < 30)
            await Task.Delay(100);
        Check(!ServerState.OnlinePresence.IsUserOnline("rimtest-" + client), "Logout left server presence online.");
    }

    async Task OfflineGiftAccept()
    {
        int before = await colonyFixture.Stock("B", "WoodLog");
        await LogoutWithSnapshot("B");
        string id = await giftScenario.Send(3, "RimTest offline gift 中文 é İ");
        Check(ServerState.Ledger.Find(id)!.Status == ServerEventStatus.PendingOfflineDelivery, "Offline gift was not queued.");
        await entry.LoginExisting(true); await eventFixture.Receive("B", id); await Idle("B");
        // Reading and choosing later must not deliver or acknowledge the stock.
        for (int i = 0; i < 2; i++)
        {
            await Invoke("B", "StartRefreshEventsForTest"); await Idle("B"); await Step("B", 300);
            Check(await colonyFixture.Stock("B", "WoodLog") == before && ServerState.Ledger.Find(id)!.AppliedSnapshotId is null,
                "Downloading an undecided gift applied it.");
        }
        await LogoutWithSnapshot("B"); await entry.LoginExisting(true); await eventFixture.Receive("B", id);
        Check(await colonyFixture.Stock("B", "WoodLog") == before, "Reconnecting accepted a deferred gift without a decision.");
        Check((await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" })).Bool("returnValue"), "Deferred gift acceptance rejected.");
        await Idle("B"); await colonyFixture.AwaitStock("B", "WoodLog", before + 3);
        await eventFixture.AwaitLedger(id, e => e.Status == ServerEventStatus.AppliedToSnapshot && e.AppliedSnapshotId is not null);
        await LogoutWithSnapshot("B"); await entry.LoginExisting(true);
        await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" }); await Idle("B"); await Step("B", 600);
        Check(await colonyFixture.Stock("B", "WoodLog") == before + 3, "Accepted offline gift duplicated after reconnect/replay.");
    }

    async Task OfflineGiftReject()
    {
        int beforeA = await colonyFixture.Stock("A", "WoodLog"), beforeB = await colonyFixture.Stock("B", "WoodLog"), cargo = await caravanFixture.Stock("WoodLog");
        string id = await giftScenario.Send(2, "RimTest reject with sender offline");
        await eventFixture.Receive("B", id); await Idle("B");
        await LogoutWithSnapshot("A");
        Check((await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Reject" })).Bool("returnValue"), "Gift rejection rejected.");
        await Idle("B"); await eventFixture.AwaitLedger(id, e => e.TargetDecision == TargetEventDecision.Rejected);
        AuthoritativeEvent[] Returns() => ServerState.Ledger.ListAll().Where(e => e.IdempotencyKey == ServerState.Ledger.Find(id)!.IdempotencyKey + ":return").ToArray();
        Check(Returns().Length == 1 && Returns()[0].Status == ServerEventStatus.PendingOfflineDelivery, "Rejected gift did not queue exactly one offline return.");
        string returnedId = Returns()[0].EventId;
        await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Reject" }); await Idle("B");
        Check(Returns().Length == 1 && await colonyFixture.Stock("B", "WoodLog") == beforeB, "Repeated rejection duplicated a return or changed recipient stock.");
        await entry.LoginExisting(true, client: "A");
        await caravanFixture.RefreshSinglePawnCaravan();
        await colonyFixture.AwaitStock("A", "WoodLog", beforeA + 2); await Idle("A");
        await eventFixture.AwaitLedger(returnedId, e => e.Status == ServerEventStatus.AppliedToSnapshot);
        Check(await caravanFixture.Stock("WoodLog") == cargo - 2, "Return was also restored to the departure caravan.");
        await LogoutWithSnapshot("A"); await entry.LoginExisting(true, client: "A");
        await caravanFixture.RefreshSinglePawnCaravan();
        await Invoke("A", "StartRefreshEventsForTest"); await Idle("A"); await Step("A", 600);
        Check(Returns().Length == 1 && await colonyFixture.Stock("A", "WoodLog") == beforeA + 2 && await caravanFixture.Stock("WoodLog") == cargo - 2,
            "Offline gift return did not conserve sender stock across reload.");
        await run.RecordAsync(new { serverObservation = "offline-gift-return", source = id, returnedId, beforeA, beforeB, cargo });
    }

    async Task OfflineNotifications()
    {
        const string durable = "RimTest durable offline notice 中文 é İ", transient = "RimTest online-only offline notice 中文 é İ";
        await LogoutWithSnapshot("B");
        foreach (var (message, persistent) in new[] { (durable, true), (transient, false) })
        {
            await Invoke("A", "StartAdminAction", new { actionKind = "Broadcast", targetUserId = "rimtest-B", message, persistentNotification = persistent });
            await Idle("A");
        }
        AuthoritativeEvent[] Notices(string message) => ServerState.Ledger.ListAll().Where(e => e.Target.UserId == "rimtest-B"
            && e.Payload is ServerNotificationEventPayload p && p.Message == message).ToArray();
        Check(Notices(durable).Length == 1 && Notices(durable)[0].Status == ServerEventStatus.PendingOfflineDelivery, "Persistent offline notice was not queued.");
        Check(Notices(transient).Length == 0, "Online-only notice entered the offline queue.");
        string id = Notices(durable)[0].EventId;
        await entry.LoginExisting(true); await eventFixture.Receive("B", id); await Idle("B");
        await eventFixture.AwaitLedger(id, e => e.Status == ServerEventStatus.AppliedToSnapshot);
        await LogoutWithSnapshot("B"); await entry.LoginExisting(true);
        await Invoke("B", "StartRefreshEventsForTest"); await Idle("B"); await Step("B", 600);
        var events = Items(await Call("B", "clash.events", new { limit = 200 }));
        Check(!events.Any(v => v.GetProperty("data").String("payloadSummary").Contains(transient)), "Online-only notification replayed after login.");
        Check(Notices(durable).Length == 1 && Notices(durable)[0].Status == ServerEventStatus.AppliedToSnapshot && Notices(transient).Length == 0,
            "Notification persistence changed after reconnect.");
    }
}
