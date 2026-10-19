using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameEconomicScenarioTests
{
    internal static void Fixtures() => FixturesAsync().GetAwaiter().GetResult();
    internal static void Mercenaries() => MercenariesAsync().GetAwaiter().GetResult();
    private static async Task FixturesAsync()
    {
        using var f = await GameScenarioTestRuntime.Create();
        var economy = new EconomicScenarioFixture(f.Run, f.Polling, new(f.Run, f.Polling));
        int pending = 1, refreshes = 0;
        f.Script = (_, method, args) => method switch
        {
            "clash.state" => new { bankInProgress = pending-- > 0 },
            "clash.invoke" => Refresh(args),
            "clash.bank" => pending < 0 ? new { marker = "current-bank" } : throw new InvalidOperationException("Read bank before refresh completed."),
            _ => null
        };
        object Refresh(JsonElement args)
        {
            Check(pending < 0 && args.String("action") == "StartRefreshBankStatus", "Bank refresh skipped initial idle or used another action.");
            refreshes++; pending = 1; return new { };
        }
        Check((await economy.RefreshBank("A")).String("marker") == "current-bank" && refreshes == 1, "Refresh did not return the final bank observation.");
        Check(f.Calls.All(c => c.Client == "A"), "Economic fixture changed client.");
        foreach (string client in new[] { "A", "B" })
        foreach (string fault in new[] { "none", "silver", "steel", "snapshot", "action" })
        {
            f.Calls.Clear(); int silver = 40, steel = 7, actions = 0; string snapshot = "baseline";
            f.Script = (_, method, _) => method switch
            {
                "clash.inventory" => Inventory(silver, steel),
                "clash.state" => new { currentSnapshotId = snapshot },
                _ => null
            };
            Task Action()
            {
                actions++;
                Check(f.Count("clash.inventory") >= 2 && f.Count("clash.state") >= 2, "Rejection assertion captured no baseline.");
                switch (fault)
                {
                    case "silver": silver--; break;
                    case "steel": steel++; break;
                    case "snapshot": snapshot = "changed"; break;
                    case "action": throw new IOException("action failed");
                }
                return Task.CompletedTask;
            }
            if (fault == "none") await economy.AssertNoMutation(client, Action);
            else if (fault == "action") await Reject<IOException>(() => economy.AssertNoMutation(client, Action), fault);
            else await Reject<AssertionException>(() => economy.AssertNoMutation(client, Action), fault);
            Check(actions == 1 && f.Calls.All(c => c.Client == client), "Rejection assertion repeated the action or crossed clients.");
        }
    }
    private static async Task MercenariesAsync()
    {
        foreach (string fault in new[] { "none", "old-spawned", "skill", "price", "undercharge", "overcharge" }) await Hire(fault);
        foreach (string label in new[] { "Load", "装载", "Charger", "Yükle" }) await Recall(label, "none");
        foreach (string fault in new[] { "rejected", "poll-failed", "start-failed" }) await Recall("装载", fault);
        foreach (string fault in new[] { "none", "price", "ratio", "tier", "wrong-colony", "undercharge", "overcharge", "snapshot", "replay-cash", "replay-snapshot", "replacement" }) await Guard(fault);
    }
    private static async Task Hire(string fault)
    {
        using var f = await GameScenarioTestRuntime.Create();
        int silver = 100, reads = 0, hires = 0; bool landed = false;
        var scenario = CreateScenario(f);
        f.Script = (_, method, args) => method switch
        {
            "clash.bank" => new { mercenaryMinDurationDays = 4 },
            "clash.inventory" => Inventory(silver),
            "clash.invoke" => Invoke(args),
            "clash.mercenaries" => Observe(),
            _ => null
        };
        object Invoke(JsonElement args)
        {
            if (args.String("action") == "StartRefreshBankStatus") return new { };
            Check(args.String("action") == "StartHireMercenary", "Wrong hire action.");
            var input = args.GetProperty("arguments");
            Check(input.String("skillDefName") == "Mining" && input.Int("skillLevel") == 7 && input.Int("durationDays") == 4, "Hire lost bank duration or skill selection.");
            hires++; silver -= 20 + (fault == "undercharge" ? -1 : fault == "overcharge" ? 1 : 0); return new { };
        }
        object Observe()
        {
            landed = ++reads >= (fault == "old-spawned" ? 3 : 2);
            return List(new { contractId = "old", completed = true, spawned = fault == "old-spawned" },
                new { contractId = "hired", completed = false, spawned = landed, actualSkill = fault == "skill" ? 6 : 7,
                    priceSilver = fault == "price" ? 0 : 20, pawn = new { handle = "hired-pawn" } });
        }
        if (fault is "none" or "old-spawned")
        {
            await scenario.Hire();
            Check(landed, "Completed historical contract bypassed waiting for the hired pawn to spawn.");
            var draft = f.Calls.Single(c => c.Method == "pawn.draft");
            Check(draft.Args.String("pawn") == "hired-pawn" && draft.Args.Bool("drafted"), "Hire drafted the wrong pawn.");
        }
        else
        {
            await Reject<AssertionException>(scenario.Hire, fault);
            Check(f.Count("pawn.draft") == 0, "Invalid hire continued to pawn actions.");
        }
        Check(hires == 1 && f.Calls.All(c => c.Client == "B") && f.Calls.Single(c => c.Method == "time.step").Args.Int("ticks") == 600,
            "Hire repeated submission, changed client or skipped landing budget.");
    }
    private static async Task Recall(string label, string fault)
    {
        using var f = await GameScenarioTestRuntime.Create();
        var contracts = new MercenaryContractRegistry();
        contracts.ActivateWithSnapshot("hire", "user", "rebuilt", "hired", "snapshot", "Mining", 7, 4, 20, 5, 10, 0, DateTimeOffset.UnixEpoch);
        var scenario = CreateScenario(f, contracts);
        bool ticking = false; int departureReads = 0;
        f.Script = (_, method, args) => method switch
        {
            "clash.mercenaries" => Observe(),
            "clash.mercenary.recall" => new { accepted = fault != "rejected" },
            "clash.labels" => new { load = label },
            "gizmos.list" => List(new { label, type = "disabled-load", disabled = true }, new { label = "other", type = "other", disabled = false }, new { label, type = "original-load", disabled = false }),
            "time.run" => Start(),
            _ => null
        };
        object Start()
        {
            ticking = true;
            if (fault == "start-failed") throw new IOException("run response failed after starting ticks");
            return new { };
        }
        object Observe()
        {
            if (ticking && fault == "poll-failed") throw new IOException("recall observation failed");
            return List(new { reference = new { handle = "contract-reference" }, contractId = "hired", completed = ticking && ++departureReads >= 2,
                pawn = new { handle = "hired-pawn" }, shuttle = new { handle = "original-shuttle" } },
                new { contractId = "historical", completed = true });
        }
        if (fault == "rejected")
        {
            await Reject<AssertionException>(scenario.Recall, fault);
            Check(f.Count("time.step") == 0 && f.Count("time.run") == 0, "Rejected recall continued.");
            return;
        }
        if (fault == "none")
        {
            var completion = scenario.Recall();
            try
            {
                // Wait for the client-side exit, then hold the real registry open deliberately.
                for (int i = 0; i < 100 && f.Count("time.cancel") == 0 && !completion.IsCompleted; i++) await Task.Delay(20);
                Check(f.Count("time.cancel") == 1 && !completion.IsCompleted, "Recall accepted client-only completion without the authoritative contract.");
            }
            finally { contracts.Complete("hired", "user", "rebuilt"); await completion; }
        }
        else await Reject<IOException>(scenario.Recall, fault);
        Check(f.Count("time.cancel") == 1, "Recall left ticking active after " + fault);
        var load = f.Calls.Single(c => c.Method == "gizmos.invoke");
        Check(load.Args.String("handle") == "original-shuttle" && load.Args.String("label") == label && load.Args.String("type") == "original-load", "Recall bypassed localized enabled load command.");
        Check(f.Calls.Single(c => c.Method == "clash.mercenary.recall").Args.String("contract") == "contract-reference"
            && !f.Calls.Single(c => c.Method == "pawn.draft").Args.Bool("drafted") && f.Calls.All(c => c.Client == "B"), "Recall used another contract/client or kept pawn drafted.");
    }
    private static async Task Guard(string fault)
    {
        using var f = await GameScenarioTestRuntime.Create();
        var guards = new MercenaryGuardContractRegistry();
        int price = 30, quoteReads = 0, silver = 0, purchases = 0; float ratio = 0.4f; string snapshot = "before";
        var scenario = CreateScenario(f, guards: guards, quote: () => { quoteReads++; return (price, ratio); });
        f.Script = (_, method, args) => method switch
        {
            "clash.state" => new { userId = "rebuilt-user", colonyId = "rebuilt-colony", currentSnapshotId = snapshot },
            "clash.inventory" => Inventory(silver),
            "clash.fixture.bankCash" => Cash(args),
            "clash.invoke" => Purchase(args),
            _ => null
        };
        object Cash(JsonElement args)
        {
            Check(args.Bool("acknowledgeFixture") && args.Int("amount") == price * 2, "Guard fixture did not reserve cash for a meaningful replay.");
            silver = args.Int("amount"); return new { };
        }
        object Purchase(JsonElement args)
        {
            Check(args.String("action") == "StartHireMercenaryGuard" && args.GetProperty("arguments").String("tier") == "Apprentice", "Wrong guard action/tier.");
            if (++purchases == 1)
            {
                guards.ActivateWithSnapshot("first", "rebuilt-user", fault == "wrong-colony" ? "old-colony" : "rebuilt-colony", "guard", "after",
                    fault == "tier" ? "Veteran" : "Apprentice", fault == "price" ? price + 1 : price, fault == "ratio" ? ratio + 0.1f : ratio, 0, DateTimeOffset.UnixEpoch);
                silver -= price + (fault == "undercharge" ? -1 : fault == "overcharge" ? 1 : 0);
                if (fault != "snapshot") snapshot = "after";
                // A later configuration read must not replace this purchase's captured quote.
                price = 60; ratio = 0.8f;
            }
            else
            {
                if (fault == "replay-cash") silver--;
                if (fault == "replay-snapshot") snapshot = "replayed";
                if (fault == "replacement")
                {
                    guards.ConsumeForRaid("rebuilt-user", "rebuilt-colony", "raid", "guard", DateTimeOffset.UnixEpoch);
                    guards.ActivateWithSnapshot("second", "rebuilt-user", "rebuilt-colony", "replacement", "after", "Apprentice", 30, 0.4f, 0, DateTimeOffset.UnixEpoch);
                }
            }
            return new { };
        }
        if (fault == "none") { await scenario.GuardPurchase(); Check(purchases == 2, "Guard skipped duplicate purchase check."); }
        else await Reject<AssertionException>(scenario.GuardPurchase, fault);
        Check(quoteReads == 1 && f.Calls.All(c => c.Client == "B"), "Guard mixed configuration versions or crossed clients.");
    }
    private static MercenaryScenario CreateScenario(GameScenarioTestRuntime f, MercenaryContractRegistry? contracts = null,
        MercenaryGuardContractRegistry? guards = null, Func<(int, float)>? quote = null)
    {
        var colony = new ColonyScenarioFixture(f.Run, f.Polling);
        return new(f.Run, f.Polling, colony, new(f.Run, f.Polling, colony), contracts ?? new(), guards ?? new(), quote ?? (() => (30, 0.4f)));
    }
    private static object Inventory(int silver, int steel = 0) => new { maps = new[] {
        new { items = new Dictionary<string, int> { ["Silver"] = silver / 2, ["Steel"] = steel } },
        new { items = new Dictionary<string, int> { ["Silver"] = silver - silver / 2 } } } };
    private static object List(params object[] items) => new { items };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action, string context) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + context);
    }
}
