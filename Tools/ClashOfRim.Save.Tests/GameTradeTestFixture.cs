using System.Text.Json;
using System.Text.Json.Nodes;
using ClashOfRim.GameTests;
using RimTest.Protocol;

internal sealed class GameTradeTestFixture : IDisposable
{
    internal static readonly string[] ComplexCases = ["complex-item-fixture", "complex-item-cancel-refund", "complex-item-trade-delivery"];
    internal readonly GameScenarioTestRuntime Runtime;
    internal readonly TradeScenarioFixture Actions;
    internal readonly TradeScenario Trades;
    internal readonly ComplexItemScenario Complex;
    internal readonly Dictionary<string, Dictionary<string, int>> Stock = new()
    { ["A"] = new() { ["Steel"] = 100, ["WoodLog"] = 100 }, ["B"] = new() { ["Steel"] = 100, ["WoodLog"] = 100 } };
    internal readonly Dictionary<string, int> Cargo = new() { ["Steel"] = 3, ["WoodLog"] = 20 };
    internal readonly List<List<string>> Offers = [];
    internal int Creates, Cancels, Accepts, MemoCancels, Deliveries, DelayedReads;
    private readonly string fault, label;
    private readonly Dictionary<string, string> leases = [], scopes = [], dialogs = [];
    private readonly Dictionary<string, List<JsonElement>> items = new() { ["A"] = [], ["B"] = [] };
    private readonly List<string> staged = [];
    private string seller = "A", id = "", snapshot = "before";
    private bool active, accepted, custom;
    private int serial, hiddenReads, staleAcceptance, staleMemoCount;
    private GameTradeTestFixture(GameScenarioTestRuntime runtime, string fault, string label)
    {
        Runtime = runtime; this.fault = fault; this.label = label; runtime.Run.Progress = null;
        var colony = new ColonyScenarioFixture(runtime.Run, runtime.Polling);
        var caravan = new CaravanScenario(runtime.Run, runtime.Polling, runtime.Dialogs, colony);
        caravan.Use(Wire.Element(Reference("expedition")));
        Actions = new(runtime.Run, runtime.Polling, runtime.Dialogs);
        Trades = new(runtime.Run, runtime.Polling, runtime.Dialogs, colony, new(runtime.Run, runtime.Polling, colony), caravan, Actions);
        Complex = new(runtime.Run, runtime.Polling, colony, Actions);
        runtime.Script = Reply;
    }
    internal static async Task<GameTradeTestFixture> Create(string fault = "none", string label = "Confirm")
        => new(await GameScenarioTestRuntime.Create(plannedCases: ComplexCases), fault, label);
    internal object? Reply(string client, string method, JsonElement args)
    {
        switch (method)
        {
            case "clash.state": return new { currentSnapshotId = snapshot };
            case "clash.inventory": return new { maps = new[] { new { items = Stock[client] } } };
            case "caravan.state":
                Check(client == "A" && args.String("caravan") == "expedition", "Trade used the wrong caravan.");
                return List(Cargo.Select(p => (object)new { defName = p.Key, count = p.Value }).ToArray());
            case "game.maps": return List(Reference(client + "-home"));
            case "designators.list": return List(new { reference = new { handle = "stockpile", type = "RimWorld.Designator_ZoneAddStockpile_Resources" } });
            case "map.things": return List();
            case "ui.windows": return List(Reference("other", "Other"), Reference(client + "-editor", "Mod.TradeOrderDialogWindow"));
            case "clash.trade.beacon": Window(client, args); Check(!args.Bool("required"), "Trade changed beacon policy."); return new { };
            case "clash.trade.candidates":
                Window(client, args); Check(args.Int("limit") == 200, "Candidate limit changed.");
                return List(new { reference = new { handle = "wrong-def" }, defName = "WoodLog", count = 99 },
                    new { reference = new { handle = "too-small" }, defName = "Steel", count = 4 },
                    new { reference = new { handle = client + "-steel" }, defName = "Steel", count = 5 });
            case "clash.trade.offer":
                Window(client, args);
                if (fault == "offer-exception") throw new IOException("Interrupted editor.");
                string handle = args.String("thing");
                Check(handle == client + "-steel" ? args.Int("count") == 5 : args.Int("count") == 1 && items[client].Any(i => i.GetProperty("reference").String("handle") == handle), "Offer used a stale/wrong-client reference or changed count.");
                staged.Add(handle + ":" + args.Int("count")); return new { };
            case "clash.trade.request":
                Window(client, args); Check(args.String("defName") == "WoodLog" && args.Int("count") == 5 && args.Int("minimumHitPointsPercent") == 50, "Trade request changed."); return new { };
            case "clash.trade.submit": Window(client, args); dialogs[client] = "create"; return new { };
            case "clash.labels": return new { steamClientMissing = "Steam missing", ignore = "Ignore", quit = "Quit" };
            case "dialogs.messages": return dialogs.ContainsKey(client) ? List(new { reference = new { handle = client + "-confirm" }, text = label, buttons = new[] { new { id = "B", label = "Quit" }, new { id = "A", label } } }) : List();
            case "dialogs.choose":
                Check(args.String("dialog") == client + "-confirm" && args.String("button") == "A", "Trade bypassed the offered localized confirmation.");
                if (dialogs[client] == "create") CreateOrder(client); else CompleteCaravan();
                dialogs.Remove(client); return new { };
            case "menus.list": case "menus.options": return GameGiftTestUi.Reply(method, label, 0);
            case "menus.choose":
                Check(client == "A" && args.String("menu") == "target-menu" && args.String("option") == "recipient", "Caravan used the wrong menu choice.");
                dialogs[client] = "caravan"; return new { };
            case "clash.records": return Orders(client, args);
            case "clash.invoke": return Invoke(client, args);
            case "clash.fixture.complexItems":
                Check(client == "A" && args.Bool("acknowledgeFixture"), "Complex fixture skipped acknowledgement or changed client.");
                items["A"] = ComplexData().Select((data, i) => Wire.Element(new { reference = new { handle = "original-" + i }, data })).ToList();
                return List(items["A"].Take(fault == "incomplete" ? 5 : 6).Cast<object>().ToArray());
            case "clash.stability.items":
                return List(items[client].Where(i => (i.GetProperty("data").OptionalString("innerDef") ?? i.GetProperty("data").String("defName")) == args.String("defName")).Cast<object>().ToArray());
            default: return null;
        }
    }
    private object Orders(string client, JsonElement args)
    {
        Check(args.String("kind") == "trades", "Unexpected trade observation.");
        object other = new { reference = new { handle = "unrelated" }, data = new { eventId = "unrelated", viewerHasAccepted = true, acceptedMemoCount = 0 } };
        if (!active) return List(other); // Other orders must not block this cancellation.
        if (client != seller && hiddenReads > 0) { hiddenReads--; DelayedReads++; return List(other); }
        bool shownAccepted = accepted;
        if (client != seller && staleAcceptance > 0) { staleAcceptance--; DelayedReads++; shownAccepted = !accepted; }
        int memoCount = accepted ? 1 : 0;
        if (client == seller && staleMemoCount > 0) { staleMemoCount--; DelayedReads++; memoCount = 1; }
        leases[client] = client + "-" + id + "-" + serial++;
        object target = new { reference = new { handle = leases[client] }, data = new { eventId = id, viewerHasAccepted = shownAccepted, acceptedMemoCount = memoCount, serverDropPodPostage = new { reachable = fault != "unreachable" } } };
        return scopes.GetValueOrDefault(client) == "Mine" ? List(target) : List(other, target);
    }
    private object Invoke(string client, JsonElement args)
    {
        var input = args.GetProperty("arguments");
        switch (args.String("action"))
        {
            case "OpenTradeOrderDialog": seller = client; staged.Clear(); break;
            case "StartRefreshTradeOrders": scopes[client] = input.OptionalString("scope") ?? "All"; if (client != seller) hiddenReads = 1; break;
            case "StartCancelTradeOrder":
                Order(client, input.GetProperty("order")); Check(client == seller, "Buyer cancelled the seller order.");
                Cancels++; active = false;
                if (custom) Land("A", "refund"); else Stock[client]["Steel"] += fault == "refund" ? 4 : 5;
                break;
            case "StartAcceptTradeOrder":
                Order(client, input.GetProperty("order")); Check(!input.Bool("postagePaidByAcceptor"), "Memo changed postage payer.");
                Accepts++; accepted = true; staleAcceptance = 1;
                if (fault == "memo-buyer") Stock[client]["Steel"]++;
                if (fault == "caravan-memo") Cargo["WoodLog"]--;
                break;
            case "StartCancelAcceptedTradeMemo":
                Order(client, input.GetProperty("order")); MemoCancels++; accepted = false; staleAcceptance = staleMemoCount = 1;
                if (fault == "memo-seller") Stock[seller]["Steel"]++;
                break;
            case "StartFulfillTradeOrderByDropPod":
                Order(client, input.GetProperty("order")); Check(client == "B" && seller == "A", "Drop-pod buyer/seller changed.");
                Deliveries++; Stock["A"]["WoodLog"] += 5; Stock["B"]["WoodLog"] -= fault == "payment" ? 4 : 5;
                if (custom) { Land("B", "delivery"); if (fault == "sender-retained") Land("A", "retained"); }
                else Stock["B"]["Steel"] += fault == "delivery" ? 4 : 5;
                active = false; break;
            case "OpenCaravanTradeFulfillmentMenu":
                Check(client == "A" && input.GetProperty("caravan").String("handle") == "expedition", "Wrong caravan menu context.");
                Order(client, input.GetProperty("orders").EnumerateArray().Single()); break;
            default: throw new InvalidOperationException("Unexpected trade action: " + args.String("action"));
        }
        return new { returnValue = true };
    }
    private void CreateOrder(string client)
    {
        Creates++; Offers.Add(staged.ToList()); id = "order-" + Creates; active = true; accepted = false;
        custom = !staged.SequenceEqual(new[] { client + "-steel:5" });
        if (custom) items[client].Clear(); else Stock[client]["Steel"] -= fault == "escrow" ? 4 : 5;
    }
    private void CompleteCaravan()
    {
        Cargo["WoodLog"] -= fault == "caravan-cargo" ? 4 : 5; Cargo["Steel"] += 5; Stock["B"]["WoodLog"] += 5; active = false;
        snapshot = fault switch { "snapshot-empty" => "", "snapshot-blank" => " ", "snapshot-same" => "before", _ => "after" };
    }
    private void Order(string client, JsonElement reference) => Check(reference.String("handle") == leases[client], "Order used a stale/wrong-client reference or selected another event.");
    private static void Window(string client, JsonElement args) => Check(args.String("window") == client + "-editor", "Wrong trade editor window.");
    private void Land(string client, string phase)
    {
        items[client] = ComplexData().Select((data, i) =>
        {
            var state = JsonNode.Parse(data.GetRawText())!;
            if (state["hp"] is not null) { state["hp"] = 71; state["deterioration"] = 2; }
            if (state["rotTicks"] is not null) state["rotTicks"] = 12100;
            if (fault == phase + "-state" && i == 0) state["quality"] = "Poor";
            return Wire.Element(new { reference = new { handle = phase + "-" + i }, data = state });
        }).ToList();
        if (fault == phase + "-missing") items[client].RemoveAt(0);
        if (fault == phase + "-duplicate") items[client].Add(items[client][0]);
    }
    internal static JsonElement[] ComplexData() => Enumerable.Range(0, 6).Select(i => Wire.Element(new
    {
        defName = new[] { "Apparel_Duster", "Gun_BoltActionRifle", "MealFine", "MinifiedThing", "MinifiedThing", "MinifiedThing" }[i],
        innerDef = i < 3 ? null : new[] { "Bed", "SculptureSmall", "FueledStove" }[i - 3], count = 1,
        stuff = i == 0 ? "Cloth" : i == 3 ? "WoodLog" : null, quality = i == 2 ? null : "Masterwork", hp = i == 2 ? (int?)null : 73,
        deterioration = 0, tainted = i == 0 ? (bool?)true : null, biocoded = (bool?)null, codedPawn = (string?)null,
        ingredients = i == 2 ? new[] { "Meat_Human", "RawPotatoes" } : null, genes = (string[]?)null,
        color = "(0.2, 0.4, 0.6)", style = (string?)null, rotTicks = i == 2 ? (float?)12000 : null, fuel = i == 5 ? (float?)37 : null
    })).ToArray();
    public void Dispose() => Runtime.Dispose();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object Reference(string handle, string type = "test") => new { reference = new { handle, type } };
    private static object List(params object[] items) => new { items };
}
