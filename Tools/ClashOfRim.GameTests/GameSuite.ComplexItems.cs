using System.Text.Json;
using System.Text.Json.Nodes;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task ComplexItems()
    {
        JsonElement[] fixtures = [];
        string[] available = [];
        await Case("complex-item-fixture", async () =>
        {
            await StoreStartingResources("A"); await StoreStartingResources("B");
            fixtures = Items(await Call("A", "clash.fixture.complexItems", new { acknowledgeFixture = true }));
            Check(fixtures.Length == 6, "Complex state fixture was incomplete.");
            available = fixtures.Select(H).ToArray();
            await StoreResources("A", fixtures.Select(i => i.GetProperty("data").String("defName")).Distinct().ToArray());
        });
        await Case("complex-item-cancel-refund", async () =>
        {
            var order = await CreateTrade(available);
            await Invoke("A", "StartCancelTradeOrder", new { order = order.GetProperty("reference") }); await Idle("A");
            await Step("A", 600); await Idle("A");
            available = await VerifyComplexItems("A", fixtures, "cancellation");
            await StoreResources("A", fixtures.Select(i => i.GetProperty("data").String("defName")).Distinct().ToArray());
        });
        await Case("complex-item-trade-delivery", async () =>
        {
            int woodA = await colonyFixture.Stock("A", "WoodLog"), woodB = await colonyFixture.Stock("B", "WoodLog");
            var order = await CreateTrade(available); string id = order.GetProperty("data").String("eventId");
            await Invoke("B", "StartRefreshTradeOrders");
            var orders = await Wait("B", "clash.records", new { kind = "trades" }, p => Items(p).Any(o => o.GetProperty("data").String("eventId") == id));
            var buyer = Items(orders).Single(o => o.GetProperty("data").String("eventId") == id);
            await Invoke("B", "StartFulfillTradeOrderByDropPod", new { order = buyer.GetProperty("reference") }); await Idle("B");
            await AwaitStock("A", "WoodLog", woodA + 5); await Step("B", 600); await Idle("B");
            await VerifyComplexItems("B", fixtures, "delivery");
            Check(await colonyFixture.Stock("B", "WoodLog") == woodB - 5, "Complex trade payment was not conserved.");
            foreach (var item in fixtures)
            {
                string def = item.GetProperty("data").OptionalString("innerDef") ?? item.GetProperty("data").String("defName");
                Check(!Items(await Call("A", "clash.stability.items", new { defName = def })).Any(t => SameComplexState(item.GetProperty("data"), t.GetProperty("data"))), "Sender retained delivered complex item: " + def);
            }
        });
    }
    async Task<string[]> VerifyComplexItems(string client, JsonElement[] expected, string phase)
    {
        var handles = new List<string>(); var failures = new List<string>();
        foreach (var item in expected)
        {
            var data = item.GetProperty("data"); string def = data.OptionalString("innerDef") ?? data.String("defName");
            var received = Items(await Call(client, "clash.stability.items", new { defName = def, limit = 200 }));
            var matching = received.Where(t => SameComplexState(data, t.GetProperty("data"))).ToArray();
            await run.RecordAsync(new { observation = "complex-item-" + phase, client, defName = def, expected = data, received });
            if (matching.Length != 1) failures.Add(def + ": expected exactly one matching item, got " + matching.Length);
            else handles.Add(H(matching[0]));
        }
        Check(failures.Count == 0, "Complex state lost on " + phase + ": " + string.Join("; ", failures));
        return handles.ToArray();
    }
    static bool SameComplexState(JsonElement expected, JsonElement actual)
    {
        var e = JsonNode.Parse(expected.GetRawText())!.AsObject(); var a = JsonNode.Parse(actual.GetRawText())!.AsObject();
        // Rot legitimately advances during landing; a reset or a full new decay period does not.
        if (e["rotTicks"] is { } oldRot)
        {
            if (a["rotTicks"] is not { } newRot || newRot.GetValue<float>() < oldRot.GetValue<float>() || newRot.GetValue<float>() > oldRot.GetValue<float>() + 5000) return false;
        }
        e.Remove("rotTicks"); a.Remove("rotTicks");
        foreach (var node in new[] { e, a })
        {
            if (node["hp"] is { } hp) node["hp"] = hp.GetValue<int>() + node["deterioration"]!.GetValue<int>();
            node.Remove("deterioration");
        }
        return JsonNode.DeepEquals(e, a);
    }
}
