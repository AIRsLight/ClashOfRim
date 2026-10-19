using System.Text.Json;
using System.Text.Json.Nodes;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ComplexItemScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colonyFixture, TradeScenarioFixture trades)
{
    internal Task Run() => CreateSequence().Run(run);
    internal ScenarioSequence CreateSequence()
    {
        JsonElement[] fixtures = [];
        string[] available = [];
        return ScenarioCatalog.Items.Bind(new Dictionary<string, Func<Task>>
        {
            ["complex-item-fixture"] = async () =>
            {
                await colonyFixture.StoreStartingResources("A"); await colonyFixture.StoreStartingResources("B");
                fixtures = Items(await run.CallAsync("A", "clash.fixture.complexItems", new { acknowledgeFixture = true }));
                Check(fixtures.Length == 6, "Complex state fixture was incomplete.");
                available = fixtures.Select(H).ToArray();
                await colonyFixture.StoreResources("A", fixtures.Select(i => i.GetProperty("data").String("defName")).Distinct().ToArray());
            },
            ["complex-item-cancel-refund"] = async () =>
            {
                var order = await trades.Create(available);
                await trades.Cancel(order);
                await polling.Step("A", 600); await polling.Idle("A");
                available = await VerifyComplexItems("A", fixtures, "cancellation");
                await colonyFixture.StoreResources("A", fixtures.Select(i => i.GetProperty("data").String("defName")).Distinct().ToArray());
            },
            ["complex-item-trade-delivery"] = async () =>
            {
                int woodA = await colonyFixture.Stock("A", "WoodLog"), woodB = await colonyFixture.Stock("B", "WoodLog");
                var order = await trades.Create(available);
                await trades.FulfillByDropPod(await trades.Refresh("B", order.EventId));
                await colonyFixture.AwaitStock("A", "WoodLog", woodA + 5); await polling.Step("B", 600); await polling.Idle("B");
                await VerifyComplexItems("B", fixtures, "delivery");
                Check(await colonyFixture.Stock("B", "WoodLog") == woodB - 5, "Complex trade payment was not conserved.");
                foreach (var item in fixtures)
                {
                    string def = item.GetProperty("data").OptionalString("innerDef") ?? item.GetProperty("data").String("defName");
                    Check(!Items(await run.CallAsync("A", "clash.stability.items", new { defName = def })).Any(t => Matches(item.GetProperty("data"), t.GetProperty("data"))), "Sender retained delivered complex item: " + def);
                }
            },
        });
    }

    async Task<string[]> VerifyComplexItems(string client, JsonElement[] expected, string phase)
    {
        var handles = new List<string>(); var failures = new List<string>();
        foreach (var item in expected)
        {
            var data = item.GetProperty("data"); string def = data.OptionalString("innerDef") ?? data.String("defName");
            var received = Items(await run.CallAsync(client, "clash.stability.items", new { defName = def, limit = 200 }));
            var matching = received.Where(t => Matches(data, t.GetProperty("data"))).ToArray();
            await run.RecordAsync(new { observation = "complex-item-" + phase, client, defName = def, expected = data, received });
            if (matching.Length != 1) failures.Add(def + ": expected exactly one matching item, got " + matching.Length);
            else handles.Add(H(matching[0]));
        }
        Check(failures.Count == 0, "Complex state lost on " + phase + ": " + string.Join("; ", failures));
        return handles.ToArray();
    }
    internal static bool Matches(JsonElement expected, JsonElement actual)
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
