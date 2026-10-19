using AIRsLight.ClashOfRim.Events;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class GiftScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs, ColonyScenarioFixture colonyFixture,
    CaravanScenario caravanFixture, EventScenarioFixture events, IAuthoritativeEventLedger ledger, ServerEntryScenario entry)
{
    private string? acceptedGiftId;
    // Shared by the original gift acceptance case and the offline lifecycle cases.
    // Only player-facing gizmos/editor/confirmation initiate the transaction.
    internal async Task<string> Send(int count, string message)
    {
        string expedition = caravanFixture.Handle;
        await polling.Idle("A");
        int before = await caravanFixture.Stock("WoodLog");
        await run.InvokeClientAsync("A", "StartSyncWorldMapMarkers"); await polling.Idle("A");
        await run.CallAsync("A", "world.select", new { target = expedition });
        var gizmos = Items(await run.CallAsync("A", "world.gizmos", new { target = expedition }));
        string label = (await run.CallAsync("A", "clash.labels")).String("gift");
        var gift = gizmos.Single(g => g.String("label") == label && !g.Bool("disabled"));
        await run.CallAsync("A", "world.invokeGizmo", new { target = expedition, type = gift.String("type"), label });
        await dialogs.ChooseOnlyMenu("A");
        string window = H(Items(await run.CallAsync("A", "ui.windows")).Single(w => w.GetProperty("reference").String("type").EndsWith(".GiftDeliveryDialogWindow")));
        var wood = Items(await run.CallAsync("A", "clash.gift.inventory", new { window })).First(t => t.String("defName") == "WoodLog" && t.Int("count") >= count);
        await run.CallAsync("A", "clash.gift.select", new { window, thing = H(wood), count });
        await run.CallAsync("A", "clash.gift.submit", new { window, message });
        await dialogs.Confirm("A"); await polling.Idle("A");
        var sent = ledger.ListAll().Single(e => e.Actor.UserId == "rimtest-A" && e.Target.UserId == "rimtest-B"
            && e.Payload is ItemDeliveryEventPayload p && p.Message == message);
        Check(await caravanFixture.Stock("WoodLog") == before - count, "Gift sender cargo did not decrease exactly once.");
        await run.RecordAsync(new { serverObservation = "gift-created", sent.EventId, sent.Status, sent.DeliveryMode, count });
        return sent.EventId;
    }

    internal async Task Accept()
    {
        int before = await colonyFixture.Stock("B", "WoodLog");
        string id = await Send(5, "RimTest gift");
        await events.Receive("B", id);
        Check((await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" })).Bool("returnValue"), "Gift acceptance rejected.");
        await polling.Idle("B"); await colonyFixture.AwaitStock("B", "WoodLog", before + 5);
        acceptedGiftId = id;
    }
    internal async Task Replay()
    {
        Check(acceptedGiftId is not null, "Missing accepted gift prerequisite.");
        int before = await colonyFixture.Stock("B", "WoodLog");
        await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = acceptedGiftId, actionKind = "Accept" });
        await polling.Idle("B"); await run.InvokeClientAsync("B", "StartRefreshEventsForTest"); await polling.Step("B", 600);
        int after = await colonyFixture.Stock("B", "WoodLog");
        Check(after == before, $"Repeated accept changed gift stock from {before} to {after}.");
    }
    internal async Task OfflineAccept()
    {
        int before = await colonyFixture.Stock("B", "WoodLog");
        await entry.LogoutWithSnapshot("B");
        string id = await Send(3, "RimTest offline gift 中文 é İ");
        Check(ledger.Find(id)!.Status == ServerEventStatus.PendingOfflineDelivery, "Offline gift was not queued.");
        await entry.LoginExisting(true); await events.Receive("B", id); await polling.Idle("B");
        // Reading and choosing later must not deliver or acknowledge the stock.
        for (int i = 0; i < 2; i++)
        {
            await run.InvokeClientAsync("B", "StartRefreshEventsForTest"); await polling.Idle("B"); await polling.Step("B", 300);
            Check(await colonyFixture.Stock("B", "WoodLog") == before && ledger.Find(id)!.AppliedSnapshotId is null,
                "Downloading an undecided gift applied it.");
        }
        await entry.LogoutWithSnapshot("B"); await entry.LoginExisting(true); await events.Receive("B", id);
        Check(await colonyFixture.Stock("B", "WoodLog") == before, "Reconnecting accepted a deferred gift without a decision.");
        Check((await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" })).Bool("returnValue"), "Deferred gift acceptance rejected.");
        await polling.Idle("B"); await colonyFixture.AwaitStock("B", "WoodLog", before + 3);
        await events.AwaitLedger(id, e => e.Status == ServerEventStatus.AppliedToSnapshot && e.AppliedSnapshotId is not null);
        await entry.LogoutWithSnapshot("B"); await entry.LoginExisting(true);
        await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" }); await polling.Idle("B"); await polling.Step("B", 600);
        Check(await colonyFixture.Stock("B", "WoodLog") == before + 3, "Accepted offline gift duplicated after reconnect/replay.");
    }

    internal async Task OfflineReject()
    {
        int beforeA = await colonyFixture.Stock("A", "WoodLog"), beforeB = await colonyFixture.Stock("B", "WoodLog"), cargo = await caravanFixture.Stock("WoodLog");
        string id = await Send(2, "RimTest reject with sender offline");
        await events.Receive("B", id); await polling.Idle("B");
        await entry.LogoutWithSnapshot("A");
        Check((await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Reject" })).Bool("returnValue"), "Gift rejection rejected.");
        await polling.Idle("B"); await events.AwaitLedger(id, e => e.TargetDecision == TargetEventDecision.Rejected);
        AuthoritativeEvent[] Returns() => ledger.ListAll().Where(e => e.IdempotencyKey == ledger.Find(id)!.IdempotencyKey + ":return").ToArray();
        Check(Returns().Length == 1 && Returns()[0].Status == ServerEventStatus.PendingOfflineDelivery, "Rejected gift did not queue exactly one offline return.");
        string returnedId = Returns()[0].EventId;
        await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Reject" }); await polling.Idle("B");
        Check(Returns().Length == 1 && await colonyFixture.Stock("B", "WoodLog") == beforeB, "Repeated rejection duplicated a return or changed recipient stock.");
        await entry.LoginExisting(true, client: "A");
        await caravanFixture.RefreshSinglePawnCaravan();
        await colonyFixture.AwaitStock("A", "WoodLog", beforeA + 2); await polling.Idle("A");
        await events.AwaitLedger(returnedId, e => e.Status == ServerEventStatus.AppliedToSnapshot);
        Check(await caravanFixture.Stock("WoodLog") == cargo - 2, "Return was also restored to the departure caravan.");
        await entry.LogoutWithSnapshot("A"); await entry.LoginExisting(true, client: "A");
        await caravanFixture.RefreshSinglePawnCaravan();
        await run.InvokeClientAsync("A", "StartRefreshEventsForTest"); await polling.Idle("A"); await polling.Step("A", 600);
        Check(Returns().Length == 1 && await colonyFixture.Stock("A", "WoodLog") == beforeA + 2 && await caravanFixture.Stock("WoodLog") == cargo - 2,
            "Offline gift return did not conserve sender stock across reload.");
        await run.RecordAsync(new { serverObservation = "offline-gift-return", source = id, returnedId, beforeA, beforeB, cargo });
    }

}
