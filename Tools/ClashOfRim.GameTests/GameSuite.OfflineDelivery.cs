using System.Diagnostics;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task<AuthoritativeEvent> AwaitLedger(string id, Func<AuthoritativeEvent, bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 90)
        {
            if (ServerState.Ledger.Find(id) is { } current && predicate(current)) return current;
            await Task.Delay(100);
        }
        throw new Exception($"Ledger event {id} did not reach expected state: {ServerState.Ledger.Find(id)}");
    }

    Task<JsonElement> ReceiveEvent(string client, string id) => Wait(client, "clash.events", new { limit = 200 },
        p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id));

    async Task<int> CaravanStock(string def) => (await Call("A", "caravan.state", new { caravan = expedition }))
        .GetProperty("items").EnumerateArray().Where(t => t.String("defName") == def).Sum(t => t.Int("count"));

    // Shared by the original gift acceptance case and the offline lifecycle cases.
    // Only player-facing gizmos/editor/confirmation initiate the transaction.
    async Task<string> SendCaravanGift(int count, string message)
    {
        Check(expedition is not null, "Missing expedition.");
        await Idle("A");
        int before = await CaravanStock("WoodLog");
        await Invoke("A", "StartSyncWorldMapMarkers"); await Idle("A");
        await Call("A", "world.select", new { target = expedition });
        var gizmos = Items(await Call("A", "world.gizmos", new { target = expedition }));
        string label = (await Call("A", "clash.labels")).String("gift");
        var gift = gizmos.Single(g => g.String("label") == label && !g.Bool("disabled"));
        await Call("A", "world.invokeGizmo", new { target = expedition, type = gift.String("type"), label });
        await ChooseOnlyMenu("A");
        string window = H(Items(await Call("A", "ui.windows")).Single(w => w.GetProperty("reference").String("type").EndsWith(".GiftDeliveryDialogWindow")));
        var wood = Items(await Call("A", "clash.gift.inventory", new { window })).First(t => t.String("defName") == "WoodLog" && t.Int("count") >= count);
        await Call("A", "clash.gift.select", new { window, thing = H(wood), count });
        await Call("A", "clash.gift.submit", new { window, message });
        await Confirm("A"); await Idle("A");
        var sent = ServerState.Ledger.ListAll().Single(e => e.Actor.UserId == "rimtest-A" && e.Target.UserId == "rimtest-B"
            && e.Payload is ItemDeliveryEventPayload p && p.Message == message);
        Check(await CaravanStock("WoodLog") == before - count, "Gift sender cargo did not decrease exactly once.");
        await run.RecordAsync(new { serverObservation = "gift-created", sent.EventId, sent.Status, sent.DeliveryMode, count });
        return sent.EventId;
    }

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
        string id = await SendCaravanGift(3, "RimTest offline gift 中文 é İ");
        Check(ServerState.Ledger.Find(id)!.Status == ServerEventStatus.PendingOfflineDelivery, "Offline gift was not queued.");
        await entry.LoginExisting(true); await ReceiveEvent("B", id); await Idle("B");
        // Reading and choosing later must not deliver or acknowledge the stock.
        for (int i = 0; i < 2; i++)
        {
            await Invoke("B", "StartRefreshEventsForTest"); await Idle("B"); await Step("B", 300);
            Check(await colonyFixture.Stock("B", "WoodLog") == before && ServerState.Ledger.Find(id)!.AppliedSnapshotId is null,
                "Downloading an undecided gift applied it.");
        }
        await LogoutWithSnapshot("B"); await entry.LoginExisting(true); await ReceiveEvent("B", id);
        Check(await colonyFixture.Stock("B", "WoodLog") == before, "Reconnecting accepted a deferred gift without a decision.");
        Check((await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" })).Bool("returnValue"), "Deferred gift acceptance rejected.");
        await Idle("B"); await AwaitStock("B", "WoodLog", before + 3);
        await AwaitLedger(id, e => e.Status == ServerEventStatus.AppliedToSnapshot && e.AppliedSnapshotId is not null);
        await LogoutWithSnapshot("B"); await entry.LoginExisting(true);
        await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" }); await Idle("B"); await Step("B", 600);
        Check(await colonyFixture.Stock("B", "WoodLog") == before + 3, "Accepted offline gift duplicated after reconnect/replay.");
    }

    async Task OfflineGiftReject()
    {
        int beforeA = await colonyFixture.Stock("A", "WoodLog"), beforeB = await colonyFixture.Stock("B", "WoodLog"), cargo = await CaravanStock("WoodLog");
        string id = await SendCaravanGift(2, "RimTest reject with sender offline");
        await ReceiveEvent("B", id); await Idle("B");
        await LogoutWithSnapshot("A");
        Check((await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Reject" })).Bool("returnValue"), "Gift rejection rejected.");
        await Idle("B"); await AwaitLedger(id, e => e.TargetDecision == TargetEventDecision.Rejected);
        AuthoritativeEvent[] Returns() => ServerState.Ledger.ListAll().Where(e => e.IdempotencyKey == ServerState.Ledger.Find(id)!.IdempotencyKey + ":return").ToArray();
        Check(Returns().Length == 1 && Returns()[0].Status == ServerEventStatus.PendingOfflineDelivery, "Rejected gift did not queue exactly one offline return.");
        string returnedId = Returns()[0].EventId;
        await Invoke("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Reject" }); await Idle("B");
        Check(Returns().Length == 1 && await colonyFixture.Stock("B", "WoodLog") == beforeB, "Repeated rejection duplicated a return or changed recipient stock.");
        await entry.LoginExisting(true, client: "A");
        expedition = H(Items(await Call("A", "caravans.list")).Single(c => c.GetProperty("pawns").GetArrayLength() == 1));
        await AwaitStock("A", "WoodLog", beforeA + 2); await Idle("A");
        await AwaitLedger(returnedId, e => e.Status == ServerEventStatus.AppliedToSnapshot);
        Check(await CaravanStock("WoodLog") == cargo - 2, "Return was also restored to the departure caravan.");
        await LogoutWithSnapshot("A"); await entry.LoginExisting(true, client: "A");
        expedition = H(Items(await Call("A", "caravans.list")).Single(c => c.GetProperty("pawns").GetArrayLength() == 1));
        await Invoke("A", "StartRefreshEventsForTest"); await Idle("A"); await Step("A", 600);
        Check(Returns().Length == 1 && await colonyFixture.Stock("A", "WoodLog") == beforeA + 2 && await CaravanStock("WoodLog") == cargo - 2,
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
        await entry.LoginExisting(true); await ReceiveEvent("B", id); await Idle("B");
        await AwaitLedger(id, e => e.Status == ServerEventStatus.AppliedToSnapshot);
        await LogoutWithSnapshot("B"); await entry.LoginExisting(true);
        await Invoke("B", "StartRefreshEventsForTest"); await Idle("B"); await Step("B", 600);
        var events = Items(await Call("B", "clash.events", new { limit = 200 }));
        Check(!events.Any(v => v.GetProperty("data").String("payloadSummary").Contains(transient)), "Online-only notification replayed after login.");
        Check(Notices(durable).Length == 1 && Notices(durable)[0].Status == ServerEventStatus.AppliedToSnapshot && Notices(transient).Length == 0,
            "Notification persistence changed after reconnect.");
    }
}
