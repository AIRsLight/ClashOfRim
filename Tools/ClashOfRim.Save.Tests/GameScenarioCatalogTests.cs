using System.Text.Json;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameScenarioCatalogTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GameScenarioPlans.json")));
        foreach (var profile in expected.RootElement.GetProperty("plans").EnumerateObject())
        {
            var plan = ScenarioCatalog.Plan(Profile(profile.Name));
            string[] ids = profile.Value.EnumerateArray().Select(x => x.GetString()!).ToArray();
            Check(plan.CaseIds.SequenceEqual(ids) && ids.Distinct().Count() == ids.Length, "Profile coverage/order changed: " + profile.Name);
            using var f = await GameScenarioTestRuntime.Create(plannedCases: plan.CaseIds); f.Run.Progress = null;
            var executed = new List<string>();
            // Bind backwards deliberately: execution order must come from the catalog, not dictionary insertion.
            var sequences = plan.Groups.Reverse().Select(g => g.Bind(g.Steps.Reverse().ToDictionary(s => s.Id,
                s => (Func<Task>)(() => { executed.Add(s.Id); return Task.CompletedTask; })))).ToArray();
            foreach (var sequence in plan.Bind(sequences)) await sequence.Run(f.Run);
            Check(executed.SequenceEqual(plan.Groups.SelectMany(g => g.Steps).Select(s => s.Id)), "Action binding changed execution order.");
            Check(f.Run.Cases.Select(c => c.Id).SequenceEqual(ids) && f.Run.NotRun.Count == 0 && f.Run.Cases.All(c => c.Status == "passed"),
                "Executed case reporting disagreed with the selected plan.");
            Check(f.Count("runtime.logs") == ids.Length * 2, "Case execution skipped or duplicated per-client log capture.");
            if (profile.Name == "features")
            {
                Check(executed.IndexOf("discover-players") == executed.IndexOf("colony-appearance-sync") + 1
                    && executed.IndexOf("refresh-features") == executed.IndexOf("raid-open-settle") + 1,
                    "Unreported feature preparation/refresh moved to another phase.");
            }
            await Reject<ArgumentException>(() => { plan.Bind(); return Task.CompletedTask; });
        }
        foreach (var bad in new[] { new SuiteProfile { GiftReplay = true }, new SuiteProfile { Stability = "bank" },
            new SuiteProfile { Features = false, Stability = "unknown" }, new SuiteProfile { Features = false, Stability = "Bank" },
            new SuiteProfile { Features = false, GiftReplay = true, Stability = "bank" }, new SuiteProfile { Features = false, Stability = "gravship" },
            new SuiteProfile { Features = false, Stability = "catalog-sample" } })
            await Reject<InvalidDataException>(() => { ScenarioCatalog.Plan(bad); return Task.CompletedTask; });
        await BindingFailures(); await FailurePolicy(); await WorldBindings(); await RefreshAndDiscovery();
    }
    private static SuiteProfile Profile(string name) => name switch
    {
        "features" => new(), "world-only" => new() { Features = false }, "gift-replay" => new() { Features = false, GiftReplay = true },
        _ => new() { Features = false, Stability = name, Expansions = name == "gravship" ? ["Odyssey"] : [],
            CatalogDefinitions = name == "catalog-sample" ? ["Steel"] : null }
    };
    private static async Task BindingFailures()
    {
        var group = ScenarioCatalog.Startup;
        var actions = group.Steps.ToDictionary(s => s.Id, _ => (Func<Task>)(() => Task.CompletedTask));
        actions.Remove("launch-profile");
        await Reject<ArgumentException>(() => { group.Bind(actions); return Task.CompletedTask; });
        actions.Add("launch-profile", () => Task.CompletedTask); actions.Add("unexpected", () => Task.CompletedTask);
        await Reject<ArgumentException>(() => { group.Bind(actions); return Task.CompletedTask; });
        await Reject<ArgumentException>(() => { group.Bind(("launch-profile", () => Task.CompletedTask), ("launch-profile", () => Task.CompletedTask)); return Task.CompletedTask; });
        actions.Remove("unexpected");
        var bound = group.Bind(actions);
        actions["launch-profile"] = () => throw new InvalidOperationException("Binding retained mutable dictionary.");
        using var f = await GameScenarioTestRuntime.Create(plannedCases: group.CaseIds); f.Run.Progress = null;
        await bound.Run(f.Run);
        Check(f.Run.Cases.Count == 2, "Bound actions were replaced after composition.");
    }
    private static async Task FailurePolicy()
    {
        var group = ScenarioCatalog.World;
        foreach (string fault in new[] { "observations", "storyteller-permission", "runtime-difficulty-sync", "capture-world" })
        {
            using var f = await GameScenarioTestRuntime.Create(plannedCases: group.CaseIds); f.Run.Progress = null;
            var invoked = new List<string>();
            var sequence = group.Bind(group.Steps.ToDictionary(s => s.Id, s => (Func<Task>)(() =>
            {
                invoked.Add(s.Id);
                if (s.Id == fault || fault == "observations" && s.ContinueOnFailure) throw new IOException("failure in " + s.Id);
                return Task.CompletedTask;
            })));
            if (fault == "observations")
            {
                await sequence.Run(f.Run);
                Check(f.Run.Cases.Count(c => c.Status == "failed") == 7 && f.Run.Cases.Count(c => c.Status == "passed") == 2 && f.Run.NotRun.Count == 0,
                    "Independent world observations stopped early or lost failed results.");
                Check(!await f.Run.SaveReportAsync(true), "Continued observation failures produced a passing report.");
            }
            else
            {
                await Reject<IOException>(() => sequence.Run(f.Run));
                Check(invoked.Last() == fault, "Execution continued after a prerequisite/action failure.");
                if (fault == "capture-world") Check(f.Run.Cases.Count == 0 && f.Run.NotRun.Count == 9, "Unreported preparation invented a case result.");
                else Check(f.Run.Cases.Last().Id == fault && f.Run.Cases.Last().Status == "failed"
                    && f.Run.NotRun.Count == (fault == "storyteller-permission" ? 1 : 0), "Strict failure lost attribution or not-run cases.");
            }
        }
    }
    private static async Task WorldBindings()
    {
        using var f = await GameScenarioTestRuntime.Create(plannedCases: ScenarioCatalog.World.CaseIds); f.Run.Progress = null;
        var world = new WorldScenario(f.Run, f.Polling, new(), GameWorldTestFixture.ReadyRegistry(), new AIRsLight.ClashOfRim.Network.StraightLineWorldTileDistanceCalculator(),
            _ => throw new IOException("persistence evidence unavailable"));
        f.Script = (client, method, _) => method switch
        {
            "clash.world.details" => new { difficultyValues = new { threatScale = 1 } },
            "clash.storyteller.open" => client == "B" ? new { opened = false } : throw new IOException("runtime difficulty action failed"),
            _ => null
        };
        var sequence = world.CreateSequence();
        Check(f.Calls.Count == 0, "World binding performed observations before execution.");
        await Reject<IOException>(() => sequence.Run(f.Run));
        Check(f.Run.Cases.Count(c => c.Status == "failed") == 8 && f.Run.Cases.Single(c => c.Status == "passed").Id == "storyteller-permission"
            && f.Run.Cases.Last().Id == "runtime-difficulty-sync" && f.Count("clash.world.details") == 4,
            "Actual world bindings changed independent failures, preparation or strict action attribution.");
    }
    private static async Task RefreshAndDiscovery()
    {
        using var f = await GameScenarioTestRuntime.Create();
        var colony = new ColonyScenarioFixture(f.Run, f.Polling);
        var refresh = new FeatureRefreshScenario(f.Run, new(f.Run, f.Polling, colony));
        f.Script = (client, method, _) => method switch
        {
            "clash.state" => new { isInActiveMultiplayerSession = client == "A" },
            "clash.records" => new { items = new[] { new { reference = new { handle = "player-B" }, data = new { userId = "rimtest-B" } } } },
            _ => null
        };
        await refresh.Run(["A", "B"]);
        var actions = f.Calls.Where(c => c.Method == "clash.invoke").ToArray();
        Check(actions.All(c => c.Client == "A") && actions.Select(c => c.Args.String("action")).SequenceEqual(new[]
            { "StartRefreshBankStatus", "StartRefreshAchievements", "StartRefreshTradeOrders", "StartRefreshServerShop", "StartSyncWorldMapMarkers" }),
            "Feature refresh changed order or included a disconnected client.");
        Check(f.Calls.Single(c => c.Method == "clash.records").Args.String("kind") == "markers", "Feature refresh omitted marker observation.");
        f.Calls.Clear(); await new PlayerScenarioFixture(f.Run, f.Polling).DiscoverBoth();
        Check(f.Calls.Where(c => c.Method == "clash.invoke").Select(c => c.Client).SequenceEqual(new[] { "A", "B" })
            && f.Calls.Last().Client == "A" && f.Calls.Last().Args.String("kind") == "players", "Player discovery changed observer or refresh order.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
