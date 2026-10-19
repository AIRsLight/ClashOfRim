using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameTransferScenarioTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        await Fixtures();
        foreach (string mode in new[] { "normal", "unknown-pawn", "short-cargo", "path-rejected" }) await Travel(mode);
        foreach (string language in new[] { "English", "中文", "Français", "Türkçe" }) await Gift(language);
        foreach (int discrepancy in new[] { -1, 1 }) await Gift("cargo-error", discrepancy);
    }
    private static async Task Fixtures()
    {
        using var f = await GameScenarioTestRuntime.Create();
        var colony = new ColonyScenarioFixture(f.Run, f.Polling); int observations = 0, enabled = 1;
        f.Script = (_, method, args) => method switch
        {
            "game.maps" => List(Ref("home")),
            "designators.list" => List(Ref("wrong", "Other"), Ref("stockpile", "RimWorld.Designator_ZoneAddStockpile_Resources")),
            "map.things" => args.String("defName") == "Steel" ? List(new { x = 1, z = 2 }, new { x = 1, z = 2 }, new { x = 2, z = 2 }) : List(),
            "clash.inventory" => new { maps = new[] { new { items = new Dictionary<string, int> { ["Steel"] = ++observations == 1 ? 0 : 5 } } } },
            "menus.list" => List(Ref("menu")),
            "menus.options" => List(Enumerable.Range(0, 3).Select(i => (object)new { reference = new { handle = "option-" + i }, disabled = i >= enabled }).ToArray()),
            _ => null
        };
        await colony.StoreResources("B", ["Steel", "Gold"]);
        var cells = f.Calls.Single(c => c.Method == "designator.cells");
        Check(cells.Client == "B" && cells.Args.String("map") == "home" && cells.Args.String("designator") == "stockpile"
            && cells.Args.GetProperty("cells").GetArrayLength() == 2, "Resource fixture used wrong map/tool or repeated occupied cells.");
        await colony.AwaitStock("B", "Steel", 5);
        Check(f.Count("time.step") == 1 && f.Calls.Single(c => c.Method == "time.step").Args.Int("ticks") == 300, "Stock fixture ticked after reaching its target or skipped landing ticks.");
        await f.Dialogs.ChooseOnlyMenu("B");
        Check(f.Calls.Last().Args.String("option") == "option-0", "Menu selected a disabled option.");
        foreach (int count in new[] { 0, 2 })
        {
            enabled = count;
            await Reject<AssertionException>(() => f.Dialogs.ChooseOnlyMenu("B"));
        }
        Check(f.Count("menus.choose") == 1, "Ambiguous menu continued.");
    }
    private static async Task Travel(string mode)
    {
        using var f = await GameScenarioTestRuntime.Create();
        var caravan = new CaravanScenario(f.Run, f.Polling, f.Dialogs, new(f.Run, f.Polling)); int progress = 0;
        string handle = "departed";
        f.Script = (client, method, args) => method switch
        {
            "game.maps" => List(Ref("home")),
            "designators.list" => List(Ref("stockpile", "RimWorld.Designator_ZoneAddStockpile_Resources")),
            "map.things" => List(),
            "pawns.list" => List(new { reference = new { handle = "npc" }, playerControlled = false }, new { reference = new { handle = "chosen" }, playerControlled = true }),
            "world.neighbors" => new { neighbors = new[] { new { passable = false, tile = new { tileId = 10 } }, new { passable = true, tile = new { tileId = 11 } } } },
            "caravan.prepare" => Ref("draft"),
            "caravan.items" => List(Group("person", "Human", 1, "chosen"), Group("too-small", "WoodLog", 9),
                Group("wood", "WoodLog", mode == "short-cargo" ? 9 : 10), Group("meals", "MealSurvivalPack", 10)),
            "caravans.list" => List(new { reference = new { handle = "unrelated" }, pawns = new[] { new { handle = "npc" }, new { handle = "other" } } },
                new { reference = new { handle }, pawns = new[] { new { handle = "chosen" } } }),
            "clash.inventory" => new { maps = new[] { new { tileId = 90, layerId = 0, items = new Dictionary<string, int> { ["WoodLog"] = 20, ["MealSurvivalPack"] = 20 } } } },
            "caravan.move" => new { accepted = mode != "path-rejected" },
            "caravan.state" => new { moving = ++progress == 2, tile = new { tileId = progress == 1 ? 89 : 90 },
                items = new[] { new { defName = "WoodLog", count = 4 }, new { defName = "Steel", count = 100 }, new { defName = "WoodLog", count = 6 } } },
            _ => null
        };
        if (mode != "normal")
        {
            await Reject<AssertionException>(() => caravan.Travel(mode == "unknown-pawn" ? "npc" : "chosen"));
            Check(f.Count("caravan.submit") == (mode == "path-rejected" ? 1 : 0), "Invalid pawn/cargo was submitted.");
            Check(f.Count("time.run") == (mode == "path-rejected" ? 1 : 0), "Rejected travel continued simulation.");
            return;
        }
        await caravan.Travel("chosen");
        Check(caravan.Handle == handle && progress == 3 && f.Count("time.cancel") == 2, "Travel accepted wrong/still-moving destination or kept stale handle.");
        Check(f.Calls.Single(c => c.Method == "caravan.prepare").Args.Int("destination") == 11, "Caravan selected impassable departure neighbour.");
        Check(f.Calls.Where(c => c.Method == "caravan.select").Select(c => (c.Args.String("group"), c.Args.Int("count")))
            .SequenceEqual(new[] { ("person", 1), ("wood", 10), ("meals", 10) }), "Caravan selected wrong pawn/cargo/count.");
        Check(f.Calls.Single(c => c.Method == "pawn.draft").Args.String("pawn") == "chosen", "Travel changed uncontrolled pawn.");
        handle = "restored"; await caravan.RefreshSinglePawnCaravan();
        Check(caravan.Handle == "restored" && await caravan.Stock("WoodLog") == 10
            && f.Calls.Last().Args.String("caravan") == "restored", "Reloaded caravan stock used an expired handle or counted other items.");
        var independent = new CaravanScenario(f.Run, f.Polling, f.Dialogs, new(f.Run, f.Polling));
        await Reject<InvalidOperationException>(() => independent.Stock("WoodLog"));
        caravan.Use(Wire.Element(Ref("support-return")));
        Check(caravan.Handle == "support-return", "Support return did not replace the departed caravan.");
    }
    private static async Task Gift(string language, int discrepancy = 0)
    {
        using var f = await GameScenarioTestRuntime.Create();
        var ledger = new InMemoryAuthoritativeEventLedger(); var colony = new ColonyScenarioFixture(f.Run, f.Polling);
        var caravan = new CaravanScenario(f.Run, f.Polling, f.Dialogs, colony); caravan.Use(Wire.Element(Ref("sender-caravan")));
        var events = new EventScenarioFixture(f.Polling, ledger);
        var gift = new GiftScenario(f.Run, f.Polling, f.Dialogs, colony, caravan, events, ledger, f.CreateEntry());
        int cargo = 10, recipient = 20, selected = 0, receives = 0; bool applied = false, duplicate = false; string id = "";
        string label = language + " gift";
        f.Script = (_, method, args) => method switch
        {
            "clash.inventory" => new { maps = new[] { new { items = new Dictionary<string, int> { ["WoodLog"] = recipient } } } },
            "clash.gift.select" => Select(args),
            "clash.gift.submit" => Send(args),
            "clash.events" => List(new { data = new { eventId = ++receives == 1 ? "unrelated-event" : id } }),
            "clash.invoke" when args.String("action") == "HandleEventLetterAction" => Accept(args),
            _ => GameGiftTestUi.Reply(method, label, cargo)
        };
        object Select(JsonElement args) { selected = args.Int("count"); return new { }; }
        object Send(JsonElement args)
        {
            string message = args.String("message");
            ledger.Append(CreateGift("wrong-target", "rimtest-C", message));
            var sent = CreateGift("selected", "rimtest-B", message); ledger.Append(sent); id = sent.EventId;
            cargo -= selected + discrepancy; return new { };
        }
        object Accept(JsonElement args)
        {
            Check(args.GetProperty("arguments").String("eventId") == id, "Gift accepted another event.");
            if (!applied || duplicate) recipient += selected;
            applied = true; return new { returnValue = true };
        }
        await Reject<AssertionException>(gift.Replay);
        if (discrepancy != 0)
        {
            await Reject<AssertionException>(gift.Accept);
            Check(!applied, "Incorrect sender cargo reached gift acceptance."); return;
        }
        await gift.Accept();
        Check(receives == 2 && recipient == 25 && cargo == 5 && f.Count("clash.gift.submit") == 1, "Gift selected wrong event or transferred wrong amount.");
        var gizmo = f.Calls.Single(c => c.Method == "world.invokeGizmo").Args;
        Check(gizmo.String("label") == label && gizmo.String("type") == "gift-command" && gizmo.String("target") == "sender-caravan", "Gift ignored localized/enabled gizmo or caravan ownership.");
        Check(f.Calls.Single(c => c.Method == "clash.gift.select").Args.String("thing") == "wood", "Gift selected insufficient cargo.");
        await gift.Replay();
        duplicate = true; await Reject<AssertionException>(gift.Replay);
        var independent = new GiftScenario(f.Run, f.Polling, f.Dialogs, colony, caravan, events, ledger, f.CreateEntry());
        await Reject<AssertionException>(independent.Replay);
        int polls = 0;
        await events.AwaitLedger(id, e =>
        {
            if (++polls == 1)
            {
                ledger.MarkDelivered(id, "delivery-snapshot", DateTimeOffset.UtcNow);
                ledger.MarkApplied(id, "applied-snapshot", DateTimeOffset.UtcNow);
                return false;
            }
            return e.Status == ServerEventStatus.AppliedToSnapshot && e.AppliedSnapshotId == "applied-snapshot";
        });
        Check(polls == 2, "Ledger observation reused a stale event.");
    }
    private static AuthoritativeEvent CreateGift(string key, string target, string message) => AuthoritativeEventFactory.Create(
        ServerEventType.ItemDelivery, new("rimtest-A"), new(target), key, true, new ItemDeliveryEventPayload([], message), DateTimeOffset.UnixEpoch);
    private static object Group(string handle, string defName, int maximum, params string[] things) => new { reference = new { handle }, defName, maximum, things = things.Select(handle => new { handle }).ToArray() };
    private static object Ref(string handle, string type = "test") => new { reference = new { handle, type } };
    private static object List(params object[] items) => new { items };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
