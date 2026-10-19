using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

// A UI reference belongs to the client which observed it. Refreshing or accepting
// returns a new observation; no shared "current order" survives between scenarios.
internal sealed record TradeScenarioOrder(string Client, JsonElement Record)
{
    internal JsonElement Data => Record.GetProperty("data");
    internal JsonElement Reference => Record.GetProperty("reference");
    internal string EventId => Data.String("eventId");
}

internal sealed class TradeScenarioFixture(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs)
{
    internal async Task<TradeScenarioOrder> Create(IReadOnlyList<string>? offerReferences = null, string client = "A")
    {
        await polling.Idle(client);
        await run.InvokeClientAsync(client, "OpenTradeOrderDialog");
        var windows = Items(await run.CallAsync(client, "ui.windows"));
        string window = H(windows.Single(w => w.GetProperty("reference").String("type").EndsWith(".TradeOrderDialogWindow")));
        await run.CallAsync(client, "clash.trade.beacon", new { window, required = false });
        if (offerReferences is null)
        {
            var steel = Items(await run.CallAsync(client, "clash.trade.candidates", new { window, limit = 200 })).First(t => t.String("defName") == "Steel" && t.Int("count") >= 5);
            await run.CallAsync(client, "clash.trade.offer", new { window, thing = H(steel), count = 5 });
        }
        else foreach (string thing in offerReferences) await run.CallAsync(client, "clash.trade.offer", new { window, thing, count = 1 });
        await run.CallAsync(client, "clash.trade.request", new { window, defName = "WoodLog", count = 5, minimumHitPointsPercent = 50 });
        await run.CallAsync(client, "clash.trade.submit", new { window });
        await polling.Wait(client, "dialogs.messages", null, p => Items(p).Length == 1);
        await dialogs.Confirm(client); await polling.Idle(client);
        await run.InvokeClientAsync(client, "StartRefreshTradeOrders", new { scope = "Mine" });
        var orders = await polling.Wait(client, "clash.records", new { kind = "trades" }, p => Items(p).Length > 0);
        return new(client, Items(orders).Single());
    }
    internal async Task<TradeScenarioOrder> Refresh(string client, string eventId, string? scope = null, Func<JsonElement, bool>? matches = null)
    {
        await run.InvokeClientAsync(client, "StartRefreshTradeOrders", scope is null ? null : new { scope });
        await polling.Idle(client);
        return await AwaitOrder(client, eventId, matches);
    }
    internal async Task<TradeScenarioOrder> AcceptMemo(TradeScenarioOrder order)
    {
        await run.InvokeClientAsync(order.Client, "StartAcceptTradeOrder", new { order = order.Reference, postagePaidByAcceptor = false });
        await polling.Idle(order.Client);
        return await AwaitOrder(order.Client, order.EventId, data => data.Bool("viewerHasAccepted"));
    }
    internal async Task CancelMemo(TradeScenarioOrder order)
    {
        await run.InvokeClientAsync(order.Client, "StartCancelAcceptedTradeMemo", new { order = order.Reference });
        await polling.Idle(order.Client);
        await AwaitOrder(order.Client, order.EventId, data => !data.Bool("viewerHasAccepted"));
    }
    internal async Task Cancel(TradeScenarioOrder order)
    {
        await run.InvokeClientAsync(order.Client, "StartCancelTradeOrder", new { order = order.Reference });
        await polling.Idle(order.Client);
        await polling.Wait(order.Client, "clash.records", new { kind = "trades" }, p => !Items(p).Any(v => v.GetProperty("data").String("eventId") == order.EventId));
    }
    internal async Task FulfillByDropPod(TradeScenarioOrder order)
    {
        Check(order.Data.GetProperty("serverDropPodPostage").Bool("reachable"), "Trade delivery quote is unreachable.");
        await run.InvokeClientAsync(order.Client, "StartFulfillTradeOrderByDropPod", new { order = order.Reference });
        await polling.Idle(order.Client);
    }
    private async Task<TradeScenarioOrder> AwaitOrder(string client, string eventId, Func<JsonElement, bool>? matches = null)
    {
        var orders = await polling.Wait(client, "clash.records", new { kind = "trades" }, p => Items(p).Any(v =>
            v.GetProperty("data").String("eventId") == eventId && (matches is null || matches(v.GetProperty("data")))));
        return new(client, Items(orders).Single(v => v.GetProperty("data").String("eventId") == eventId));
    }
}
