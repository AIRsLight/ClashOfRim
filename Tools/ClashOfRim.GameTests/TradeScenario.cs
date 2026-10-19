using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class TradeScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs,
    ColonyScenarioFixture colonyFixture, EconomicScenarioFixture economy, CaravanScenario caravan, TradeScenarioFixture actions)
{
    internal async Task Cancel()
    {
        await colonyFixture.StoreStartingResources("A"); await colonyFixture.StoreStartingResources("B");
        int steel = await colonyFixture.Stock("A", "Steel");
        var order = await actions.Create();
        Check(await colonyFixture.Stock("A", "Steel") == steel - 5, "Trade creation did not reserve offered stock.");
        await actions.Cancel(order);
        await colonyFixture.AwaitStock("A", "Steel", steel); await polling.Idle("A");
    }
    internal async Task Fulfill()
    {
        int steelA = await colonyFixture.Stock("A", "Steel"), steelB = await colonyFixture.Stock("B", "Steel"), woodA = await colonyFixture.Stock("A", "WoodLog"), woodB = await colonyFixture.Stock("B", "WoodLog");
        var order = await actions.Create();
        await actions.FulfillByDropPod(await actions.Refresh("B", order.EventId));
        await colonyFixture.AwaitStock("A", "WoodLog", woodA + 5); await colonyFixture.AwaitStock("B", "Steel", steelB + 5);
        Check(await colonyFixture.Stock("A", "Steel") == steelA - 5 && await colonyFixture.Stock("B", "WoodLog") == woodB - 5, "Trade did not conserve offered/requested items.");
        await polling.Idle("A"); await polling.Idle("B");
    }
    internal async Task CancelMemo()
    {
        int steel = await colonyFixture.Stock("A", "Steel"), buyerSteel = await colonyFixture.Stock("B", "Steel"), buyerWood = await colonyFixture.Stock("B", "WoodLog");
        var order = await actions.Create();
        var buyer = await actions.AcceptMemo(await actions.Refresh("B", order.EventId));
        Check(await colonyFixture.Stock("B", "Steel") == buyerSteel && await colonyFixture.Stock("B", "WoodLog") == buyerWood, "Accepting memo prematurely delivered trade goods.");
        await actions.CancelMemo(buyer);
        await polling.Idle("A");
        order = await actions.Refresh("A", order.EventId, "Mine", data => data.Int("acceptedMemoCount") == 0);
        Check(await colonyFixture.Stock("A", "Steel") == steel - 5, "Cancelling memo changed seller escrow/order.");
        await actions.Cancel(order); await colonyFixture.AwaitStock("A", "Steel", steel);
    }
    internal async Task FulfillByCaravan()
    {
        await polling.Idle("A"); await polling.Idle("B"); await colonyFixture.StoreStartingResources("B");
        int steel = await caravan.Stock("Steel"), wood = await caravan.Stock("WoodLog");
        int sellerSteel = await colonyFixture.Stock("B", "Steel"), sellerWood = await colonyFixture.Stock("B", "WoodLog");
        string previous = (await run.CallAsync("A", "clash.state")).String("currentSnapshotId");
        var order = await actions.Create(client: "B");
        var buyer = await actions.AcceptMemo(await actions.Refresh("A", order.EventId));
        Check(await caravan.Stock("WoodLog") == wood && await caravan.Stock("Steel") == steel, "Accepting a memo changed caravan cargo.");
        await run.InvokeClientAsync("A", "OpenCaravanTradeFulfillmentMenu", new { caravan = new { handle = caravan.Handle }, orders = new[] { buyer.Reference } });
        await dialogs.ChooseOnlyMenu("A"); await dialogs.Confirm("A"); await polling.Idle("A");
        Check(await caravan.Stock("WoodLog") == wood - 5 && await caravan.Stock("Steel") == steel + 5,
            "Caravan fulfillment lost or duplicated exchanged cargo.");
        await economy.AssertSnapshotAdvanced("A", previous);
        await colonyFixture.AwaitStock("B", "WoodLog", sellerWood + 5); await polling.Idle("B");
        Check(await colonyFixture.Stock("B", "Steel") == sellerSteel - 5, "Caravan seller escrow was not conserved.");
    }
}
