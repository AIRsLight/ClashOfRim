using AIRsLight.ClashOfRim.Events;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class GiftScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs, ColonyScenarioFixture colonyFixture,
    CaravanScenario caravanFixture, EventScenarioFixture events, IAuthoritativeEventLedger ledger)
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
}
