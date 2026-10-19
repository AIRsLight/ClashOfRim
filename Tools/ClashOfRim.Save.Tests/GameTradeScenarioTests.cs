using System.Text.Json;
using System.Text.Json.Nodes;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameTradeScenarioTests
{
    internal static void Orders() => OrdersAsync().GetAwaiter().GetResult();
    internal static void Fulfillment() => FulfillmentAsync().GetAwaiter().GetResult();
    internal static void ComplexItems() => ComplexItemsAsync().GetAwaiter().GetResult();
    private static async Task OrdersAsync()
    {
        foreach (string label in new[] { "Confirm", "确认", "Confirmer", "Onayla" })
        {
            using var f = await GameTradeTestFixture.Create(label: label);
            await f.Trades.Cancel();
            Check(f.Stock["A"]["Steel"] == 100 && f.Creates == 1 && f.Cancels == 1, "Cancel failed to refund one escrow.");
            Check(f.Offers.Single().SequenceEqual(new[] { "A-steel:5" }) && f.Runtime.Calls.Count(c => c.Method == "dialogs.choose") == 1,
                "Order bypassed the original editor or confirmation.");
        }
        foreach (string fault in new[] { "escrow", "refund", "memo-buyer", "memo-seller" })
        {
            using var f = await GameTradeTestFixture.Create(fault);
            await Reject(fault.StartsWith("memo") ? f.Trades.CancelMemo : f.Trades.Cancel, fault);
        }
        using (var f = await GameTradeTestFixture.Create())
        {
            await f.Trades.CancelMemo();
            Check(f.Stock.Values.All(s => s["Steel"] == 100 && s["WoodLog"] == 100) && f.Accepts == 1 && f.MemoCancels == 1 && f.Cancels == 1,
                "Memo cancellation delivered cargo or skipped the seller refund.");
            Check(f.DelayedReads >= 4, "Memo workflow did not wait for this order, acceptance, cancellation and seller memo count.");
        }
        using (var f = await GameTradeTestFixture.Create("offer-exception"))
        {
            try { await f.Actions.Create(); throw new InvalidOperationException("Expected offer failure."); }
            catch (IOException) { }
            Check(f.Runtime.Count("clash.trade.offer") == 1 && f.Runtime.Count("clash.trade.submit") == 0, "Failed editing was retried or submitted.");
        }
        using (var first = await GameTradeTestFixture.Create())
        using (var second = await GameTradeTestFixture.Create())
        {
            var a = await first.Actions.Create(client: "A");
            var b = await second.Actions.Create(client: "B");
            await first.Actions.Cancel(a); await second.Actions.Cancel(b);
            Check(a.Client == "A" && b.Client == "B" && first.Stock["B"]["Steel"] == 100 && second.Stock["A"]["Steel"] == 100,
                "Order state or client ownership leaked between fixtures.");
        }
    }
    private static async Task FulfillmentAsync()
    {
        using (var f = await GameTradeTestFixture.Create())
        {
            await f.Trades.Fulfill();
            Check(f.Deliveries == 1 && f.Stock["A"]["Steel"] == 95 && f.Stock["A"]["WoodLog"] == 105
                && f.Stock["B"]["Steel"] == 105 && f.Stock["B"]["WoodLog"] == 95 && f.DelayedReads > 0,
                "Drop-pod fulfillment lost conservation or selected an unrelated order.");
        }
        foreach (string fault in new[] { "escrow", "payment", "delivery", "unreachable" })
        {
            using var f = await GameTradeTestFixture.Create(fault);
            await Reject(f.Trades.Fulfill, fault);
            if (fault == "unreachable") Check(f.Deliveries == 0, "Unreachable quote still executed fulfillment.");
        }
        using (var f = await GameTradeTestFixture.Create())
        {
            await f.Trades.FulfillByCaravan();
            Check(f.Cargo["Steel"] == 8 && f.Cargo["WoodLog"] == 15 && f.Stock["B"]["Steel"] == 95 && f.Stock["B"]["WoodLog"] == 105,
                "Caravan exchanged against colony inventory or lost seller escrow.");
            Check(f.Runtime.Count("menus.choose") == 1 && f.Runtime.Count("dialogs.choose") == 2 && f.Accepts == 1, "Caravan skipped memo, menu or confirmation.");
        }
        foreach (string fault in new[] { "caravan-memo", "caravan-cargo", "escrow", "snapshot-same", "snapshot-empty", "snapshot-blank" })
        {
            using var f = await GameTradeTestFixture.Create(fault);
            await Reject(f.Trades.FulfillByCaravan, fault);
        }
    }
    private static async Task ComplexItemsAsync()
    {
        var original = GameTradeTestFixture.ComplexData()[2];
        foreach (float rot in new[] { 12000f, 12001f, 17000f })
            Check(ComplexItemScenario.Matches(original, Change(original, "rotTicks", JsonValue.Create(rot))), "Legitimate rot advancement was rejected.");
        foreach (float rot in new[] { 0f, 11999f, 17001f })
            Check(!ComplexItemScenario.Matches(original, Change(original, "rotTicks", JsonValue.Create(rot))), "Rot reset/excess was accepted.");
        Check(!ComplexItemScenario.Matches(original, Change(original, "rotTicks", null)), "Rottable state was lost.");
        var durable = GameTradeTestFixture.ComplexData()[0];
        var worn = JsonNode.Parse(durable.GetRawText())!; worn["hp"] = 69; worn["deterioration"] = 4;
        Check(ComplexItemScenario.Matches(durable, Wire.Element(worn)), "Observed deterioration was treated as transfer loss.");
        worn["hp"] = 68;
        Check(!ComplexItemScenario.Matches(durable, Wire.Element(worn)), "Unobserved HP loss was ignored.");
        foreach (var (key, value) in new (string, JsonNode?)[]
        {
            ("defName", JsonValue.Create("Other")), ("innerDef", JsonValue.Create("Other")), ("count", JsonValue.Create(2)),
            ("stuff", JsonValue.Create("Steel")), ("quality", JsonValue.Create("Poor")), ("tainted", JsonValue.Create(false)),
            ("biocoded", JsonValue.Create(true)), ("codedPawn", JsonValue.Create("Other")), ("ingredients", new JsonArray("Other")),
            ("genes", new JsonArray("Other")), ("color", JsonValue.Create("red")), ("style", JsonValue.Create("Other")), ("fuel", JsonValue.Create(99f))
        }) Check(!ComplexItemScenario.Matches(durable, Change(durable, key, value)), "Complex metadata was ignored: " + key);
        Check(durable.GetProperty("hp").GetInt32() == 73 && original.GetProperty("rotTicks").GetSingle() == 12000, "State comparison modified its baseline.");

        using (var f = await GameTradeTestFixture.Create())
        {
            await f.Complex.Run();
            Check(f.Runtime.Run.Cases.Select(c => c.Id).SequenceEqual(GameTradeTestFixture.ComplexCases) && f.Runtime.Run.Cases.All(c => c.Status == "passed"), "Complex case reporting changed.");
            Check(f.Offers.Count == 2 && f.Offers[0].All(h => h.StartsWith("original-")) && f.Offers[1].All(h => h.StartsWith("refund-")), "Refund did not reacquire all item references.");
            Check(f.Offers.All(o => o.Count == 6 && o.All(h => h.EndsWith(":1"))) && f.Deliveries == 1 && f.Cancels == 1, "Complex items bypassed shared create/cancel/fulfill commands.");
            Check(f.Runtime.Calls.Where(c => c.Method == "time.step").Select(c => (c.Client, c.Args.Int("ticks"))).SequenceEqual(new[] { ("A", 600), ("B", 600) }), "Complex settlement tick budgets changed.");
            Check(f.Runtime.Count("runtime.logs") == 6, "Complex cases stopped capturing both client logs.");
            string trace = File.ReadAllText(Path.Combine(f.Runtime.Run.DirectoryPath, "trace.jsonl"));
            Check(trace.Split("\"observation\":\"complex-item-cancellation\"").Length - 1 == 6 && trace.Split("\"observation\":\"complex-item-delivery\"").Length - 1 == 6, "Complex item evidence omitted an item or phase.");
        }
        foreach (string fault in new[] { "incomplete", "refund-state", "refund-missing", "refund-duplicate", "delivery-state", "delivery-missing", "delivery-duplicate", "sender-retained", "payment", "unreachable" })
        {
            using var f = await GameTradeTestFixture.Create(fault);
            await Reject(f.Complex.Run, fault);
            string phase = fault == "incomplete" ? GameTradeTestFixture.ComplexCases[0] : fault.StartsWith("refund-") ? GameTradeTestFixture.ComplexCases[1] : GameTradeTestFixture.ComplexCases[2];
            Check(f.Runtime.Run.Cases.Last().Id == phase && f.Runtime.Run.Cases.Last().Status == "failed", "Complex failure was assigned to the wrong phase: " + fault);
            if (phase != GameTradeTestFixture.ComplexCases[2]) Check(f.Deliveries == 0, "Complex delivery proceeded after an invalid fixture/refund.");
        }
    }
    private static JsonElement Change(JsonElement original, string key, JsonNode? value)
    { var node = JsonNode.Parse(original.GetRawText())!; node[key] = value; return Wire.Element(node); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string context)
    {
        try { await action(); }
        catch (AssertionException) { return; }
        catch (Exception ex) when (ex.Message.Contains("stock mismatch for")) { return; }
        throw new InvalidOperationException("Expected trade assertion: " + context);
    }
}
