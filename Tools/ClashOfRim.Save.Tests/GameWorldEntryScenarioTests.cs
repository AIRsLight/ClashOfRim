using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.GameTests;
using ClashOfRim.TestSupport;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameWorldEntryScenarioTests
{
    internal static void World() => WorldAsync().GetAwaiter().GetResult();
    internal static void Sessions() => SessionsAsync().GetAwaiter().GetResult();
    internal static void Diagnostics() => DiagnosticsAsync().GetAwaiter().GetResult();

    private static async Task WorldAsync()
    {
        var roots = new[] { typeof(WorldScenario), typeof(ServerEntryScenario), typeof(ColonyScenarioFixture),
            typeof(SessionPersistenceScenario), typeof(ScenarioRuntimeDiagnostics), typeof(ScenarioClientActions),
            typeof(CaravanScenario), typeof(GiftScenario), typeof(EventScenarioFixture), typeof(NotificationScenario), typeof(SupportScenario), typeof(RemoteMapScenario), typeof(RaidScenario), typeof(EconomicScenarioFixture), typeof(MercenaryScenario), typeof(BankScenario),
            typeof(ShopScenario), typeof(ShopScenarioFixture), typeof(ShopCatalogScenario), typeof(ShopStressScenario), typeof(ShopVolumeFixture), typeof(ShopPersistenceEvidence), typeof(TradeScenarioFixture), typeof(TradeScenarioOrder), typeof(TradeScenario), typeof(ComplexItemScenario) };
        bool Owned(Type type) => roots.Contains(type) || type.DeclaringType is { } parent && Owned(parent);
        var violations = ModuleDependencyGuard.FindViolations(typeof(GameSuite).Assembly.GetTypes().Where(Owned).ToArray(),
            t => t == typeof(GameSuite) || t == typeof(ClashOfRimNetworkState) || t == typeof(ClashOfRimNetworkServer)
                || t == typeof(IServiceProvider) || t.FullName == "Microsoft.AspNetCore.Builder.WebApplication");
        Check(violations.Count == 0, string.Join(Environment.NewLine, violations));
        var culture = CultureInfo.CurrentCulture; var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (int coverage in new[] { 30, 50, 100 })
            foreach (bool dlc in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                using var f = await GameScenarioTestRuntime.Create();
                var profile = new SuiteProfile { Seed = "共同世界-İ-1,5", Coverage = coverage, Rainfall = "max", Temperature = "min",
                    Population = "Normal", Landmarks = "max", Pollution = 0.35f, Difficulty = "Custom",
                    CustomValues = new() { ["threatScale"] = 0.73f }, Expansions = dlc ? ["Odyssey", "Biotech", "Ideology"] : [],
                    A = new("English", "en-US"), B = new("French", name) };
                var scenario = NewWorld(f, profile);
                f.Script = (_, method, args) => method switch
                {
                    "setup.options" => new { rainfall = new[] { "Low", "Normal", "High" }, temperature = new[] { "Cold", "Normal", "Hot" },
                        population = new[] { "Low", "Normal", "High" }, landmarks = new[] { "Sparse", "Dense" } },
                    "setup.world" => args,
                    "setup.storyteller" => new { values = profile.CustomValues },
                    _ => null
                };
                scenario.VerifyLaunch("A", Hello(profile.A, profile.Expansions));
                scenario.VerifyLaunch("B", Hello(profile.B, profile.Expansions));
                await Reject<AssertionException>(() => { scenario.VerifyLaunch("B", Hello(profile.A, profile.Expansions)); return Task.CompletedTask; });
                await Reject<AssertionException>(() => { scenario.VerifyLaunch("B", Hello(profile.B, ["Royalty"])); return Task.CompletedTask; });
                await scenario.ConfigureWorld("A", "world-page"); await scenario.SelectStoryteller("A", "difficulty-page");
                var request = f.Calls.Single(c => c.Method == "setup.world").Args;
                Check(request.String("seed") == profile.Seed && request.Int("coveragePercent") == coverage
                    && request.String("rainfall") == "High" && request.String("temperature") == "Cold" && request.String("population") == "Normal",
                    "World choices changed with culture or coverage.");
                Check(request.TryGetProperty("landmarks", out var landmarks) == dlc && (!dlc || landmarks.GetString() == "Dense")
                    && request.TryGetProperty("pollution", out var pollution) == dlc && (!dlc || Math.Abs(pollution.GetDouble() - 0.35) < 1e-6),
                    "DLC-only setup values leaked or were dropped.");
                var difficulty = f.Calls.Single(c => c.Method == "setup.storyteller").Args;
                Check(difficulty.String("page") == "difficulty-page" && difficulty.String("difficulty") == "Custom"
                    && Math.Abs(difficulty.GetProperty("values").GetProperty("threatScale").GetDouble() - 0.73) < 1e-6 && !difficulty.Bool("permadeath"),
                    "Custom difficulty changed with culture.");
                await Reject<InvalidOperationException>(() => NewWorld(f, profile with { Rainfall = "unsupported" }).ConfigureWorld("A", "bad-page"));
                Check(f.Count("setup.world") == 1, "Unsupported option issued a setup mutation.");
            }
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
        await Sites();
    }

    private static async Task Sites()
    {
        foreach (bool features in new[] { false, true })
        {
            using var f = await GameScenarioTestRuntime.Create();
            var scenario = NewWorld(f, new SuiteProfile { Features = features });
            f.Script = (_, method, args) => method == "setup.sites"
                ? new { items = args.TryGetProperty("biome", out var biome) ? Array.Empty<object>() : new object[] { new { tileId = 41, layerId = 0 } } } : null;
            if (features) await Reject<AssertionException>(() => scenario.SelectSite("B", "site-page", null));
            else await scenario.SelectSite("B", "site-page", null);
            Check(f.Count("setup.sites") == (features ? 1 : 2) && f.Count("setup.site") == (features ? 0 : 1), "Climate fallback changed feature profile constraints.");
            if (!features) Check(f.Calls.Last().Args.Int("tileId") == 41, "Fallback chose an unoffered tile.");
        }
        using var near = await GameScenarioTestRuntime.Create();
        var distances = new Distances(); var world = ReadyWorld();
        var module = NewWorld(near, new SuiteProfile(), world, distances);
        near.Script = (_, method, _) => method == "setup.sites" ? new { items = new[] {
            new { tileId = 31, layerId = 0 }, new { tileId = 32, layerId = 0 }, new { tileId = 33, layerId = 0 } } } : null;
        await module.SelectSite("B", "near-page", 12);
        var query = near.Calls.Single(c => c.Method == "setup.sites").Args;
        Check(query.Int("nearTileId") == 12 && query.Int("maxDistance") == 8 && query.String("biome") == "TemperateForest", "Neighbour query lost original settlement restrictions.");
        Check(near.Calls.Last().Args.Int("tileId") == 33 && distances.Calls.Count == 3
            && distances.Calls.All(c => c.From.Tile == 12 && c.From.LayerId == c.To.LayerId && c.Overhead == 0 && c.Geometry is not null),
            "Selection accepted unknown/adjacent geometry or lost its authoritative source.");
        distances.Allow = false;
        await Reject<AssertionException>(() => module.SelectSite("B", "near-page", 12));
        Check(near.Count("setup.site") == 1, "Invalid sites bypassed registration constraints.");
    }

    private static async Task SessionsAsync()
    {
        await EntryPages();
        foreach (bool accepted in new[] { false, true })
        {
            using var f = await GameScenarioTestRuntime.Create();
            string address = "http://old-address"; int states = 0; bool invoked = false;
            var entry = NewEntry(f, () => address);
            f.Script = (_, method, _) => method switch
            {
                "clash.invoke" => Mark(),
                "clash.state" => new { isInActiveMultiplayerSession = invoked && accepted },
                "game.state" => Game(++states <= 2 ? "old" : "new"),
                "clash.inventory" => Inventory(),
                _ => null
            };
            object Mark() { invoked = true; return new { returnValue = true }; }
            address = "http://current-address";
            await entry.LoginExisting(accepted, "changed-password", "A");
            var args = f.Calls.Single(c => c.Method == "clash.invoke").Args.GetProperty("arguments");
            Check(args.String("serverBaseUrl") == address && args.String("userId") == "rimtest-A"
                && args.String("password") == "changed-password" && !args.Bool("createAccountIfMissing"), "Login used stale address or registration parameters.");
            Check(f.Count("time.step") == (accepted ? 1 : 0) && states == (accepted ? 3 : 1), "Login stabilized a rejected session or accepted the old Game epoch.");
            Check(f.Calls.All(c => c.Client == "A"), "Login touched the other client.");
        }
        using (var f = await GameScenarioTestRuntime.Create())
        {
            int states = 0;
            f.Script = (_, method, _) => method switch
            {
                "game.state" => new { startupReady = true, hasGame = ++states == 1, playing = states == 1 },
                "clash.state" => new { isInActiveMultiplayerSession = false },
                _ => null
            };
            await NewEntry(f, () => "unused").ExitSession("B");
            Check(states == 2 && f.Count("game.returnToMenu") == 1 && f.Count("clash.invoke") == 0
                && f.Calls.First().Args.Bool("discardUnsavedChanges"), "Exit used a bare logout or skipped the loaded Game teardown.");
        }
        await ColonyReload();
        await PersistenceAssertions();
    }

    private static async Task EntryPages()
    {
        using var f = await GameScenarioTestRuntime.Create();
        var entry = NewEntry(f, () => "unused"); int page = 0;
        string[] types = ["SelectScenario", "SelectStoryteller", "CreateWorldParams", "SelectStartingSite", "ConfigureStartingPawns", "ChooseIdeoPreset"];
        f.Script = (_, method, _) => method switch
        {
            "game.state" => Game("entry", control: page == types.Length),
            "clash.state" => new { isInActiveMultiplayerSession = true, currentSnapshotId = "snapshot" },
            "pages.list" => new { items = new[] { new { reference = new { handle = "page-" + page, type = "RimWorld.Page_" + types[page] } } } },
            "setup.options" => new { rainfall = new[] { "Normal" }, temperature = new[] { "Normal" }, population = new[] { "Normal" } },
            "setup.storyteller" => new { values = new { threatScale = 1.0 } },
            "setup.sites" => new { items = new[] { new { tileId = 33, layerId = 0 } } },
            "page.next" => Advance(),
            "clash.inventory" => Inventory(),
            _ => null
        };
        object Advance() { page++; return new { }; }
        entry.SetNeighbouringColony(12);
        await entry.Enter("B", "http://entry-address", register: false);
        Check(f.Count("page.next") == types.Length && f.Count("setup.storyteller") == 1 && f.Count("setup.world") == 1,
            "Entry skipped or repeated a setup page.");
        var args = f.Calls.Single(c => c.Method == "clash.invoke").Args.GetProperty("arguments");
        Check(!args.Bool("createAccountIfMissing") && args.String("serverBaseUrl") == "http://entry-address"
            && f.Calls.Single(c => c.Method == "setup.sites").Args.Int("nearTileId") == 12, "Colony rebuild lost its neighbour or became registration.");
        Check(f.Calls.Single(c => c.Method == "time.step").Args.Int("ticks") == 600 && f.Count("pawn.draft") == 3, "Entry did not settle and draft the colony once.");
        f.Script = (_, method, _) => method switch
        {
            "game.state" => Game("entry"),
            "clash.state" => new { isInActiveMultiplayerSession = true, currentSnapshotId = "snapshot" },
            "dialogs.nodes" => new { items = new[] { new { options = new[] { new { closes = true }, new { closes = false } } } } },
            _ => null
        };
        await Reject<AssertionException>(() => entry.Enter("B", "http://entry-address", register: false));
        Check(f.Count("dialog.choose") == 0 && f.Count("time.step") == 1, "Unexpected arrival choice was accepted or entry continued.");
    }

    private static async Task ColonyReload()
    {
        using var f = await GameScenarioTestRuntime.Create();
        var colony = new ColonyScenarioFixture(f.Run, f.Polling); int states = 0;
        f.Script = (_, method, _) => method switch
        {
            "clash.inventory" => Inventory(),
            "game.state" => ++states switch { <= 2 => Game("old"), 3 => Game("new", control: false), 4 => Game("new", maps: 0), _ => Game("new") },
            _ => null
        };
        Check(await colony.Stock("A", "Silver") == 27 && await colony.Stock("B", "Steel") == 5 && await colony.Stock("A", "Gold") == 0,
            "Inventory total omitted maps or invented missing stock.");
        await colony.Reload("B", "roundtrip");
        var saved = f.Calls.Single(c => c.Method == "game.save").Args.String("name");
        Check(states == 5 && saved.StartsWith("roundtrip-", StringComparison.Ordinal)
            && saved == f.Calls.Single(c => c.Method == "game.load").Args.String("name") && f.Count("time.set") == 2,
            "Reload accepted a stale/unready Game or loaded a different save.");
        states = 0; await colony.Reload("B", "roundtrip");
        Check(f.Calls.Last(c => c.Method == "game.save").Args.String("name") != saved, "Repeated reload reused a stale save name.");
        await colony.Stabilize("A");
        Check(f.Calls.Where(c => c.Method == "pawn.draft").Select(c => c.Args.String("pawn")).SequenceEqual(new[] { "pawn-1", "pawn-2", "pawn-3" })
            && f.Calls.Where(c => c.Method == "pawn.draft").All(c => c.Client == "A" && c.Args.Bool("drafted")), "Stabilization drafted another map/client's pawns.");
    }

    private static async Task DiagnosticsAsync()
    {
        const string resolution = "Resolution too small (0x0), resetting UI scale to 1.";
        foreach (bool headless in new[] { false, true })
        {
            using var f = await GameScenarioTestRuntime.Create(); f.Headless = headless;
            var diagnostics = new ScenarioRuntimeDiagnostics(f.Run);
            f.Logs["A"].Add(Log(1, "error", resolution));
            await diagnostics.CaptureStartup("A"); await diagnostics.CaptureStartup("B");
            if (headless) { diagnostics.CheckEntry("A", f.Logs["A"][0]); await diagnostics.Verify(["A", "B"]); }
            else
            {
                await Reject<AssertionException>(() => { diagnostics.CheckEntry("A", f.Logs["A"][0]); return Task.CompletedTask; });
                await Reject<AssertionException>(() => diagnostics.Verify(["A", "B"]));
            }
            f.Logs["B"].Add(Log(1, "error", resolution));
            await diagnostics.CaptureStartup("B"); // Re-entry must not exempt newly produced errors.
            await Reject<AssertionException>(() => { diagnostics.CheckEntry("B", f.Logs["B"][0]); return Task.CompletedTask; });
            f.Logs["A"].Add(Log(2, "error", resolution));
            await diagnostics.CaptureStartup("A");
            await Reject<AssertionException>(() => { diagnostics.CheckEntry("A", f.Logs["A"][1]); return Task.CompletedTask; });
            await Reject<AssertionException>(() => diagnostics.Verify(["A", "B"]));
        }
        foreach (var (level, message) in new[] { ("error", "ordinary failure"), ("bridge", "bridge failure"), ("probe", "probe failure"),
            ("warning", "[ClashOfRim] Main thread action failed: detail"), ("info", "[ClashOfRim][AutoEventApply] prepare failed detail"),
            ("error", resolution + " extra") })
        {
            using var f = await GameScenarioTestRuntime.Create(); var diagnostics = new ScenarioRuntimeDiagnostics(f.Run);
            f.Logs["A"].Add(Log(1, "info", "ordinary startup")); f.Logs["A"].Add(Log(2, level, message));
            await diagnostics.CaptureStartup("A");
            await Reject<AssertionException>(() => diagnostics.Verify(["A"]));
        }
    }

    private static async Task PersistenceAssertions()
    {
        string[] fields = ["worldConfigurationId", "info", "layers", "storyteller", "difficulty", "difficultyValues", "features", "factions", "ideologies", "pawns"];
        foreach (bool reconnect in new[] { false, true })
        foreach (string changed in new[] { "none", "stock", "colony" }.Concat(fields))
        {
            using var f = await GameScenarioTestRuntime.Create();
            bool restored = false, active = true; int epoch = 0;
            var before = fields.ToDictionary(key => key, key => (object)new { value = "original-" + key });
            var after = new Dictionary<string, object>(before);
            if (fields.Contains(changed)) after[changed] = new { value = "corrupted" };
            f.Script = (_, method, args) => method switch
            {
                "clash.state" => new { isInActiveMultiplayerSession = active, colonyId = restored && changed == "colony" ? "different-colony" : "colony" },
                "clash.world.details" => restored ? after : before,
                "clash.inventory" => Inventory(restored && changed == "stock" ? 19 : 20),
                "game.state" => new { epoch = epoch.ToString(CultureInfo.InvariantCulture), playerHasControl = active, playing = active,
                    startupReady = !active, hasGame = active, mapCount = active ? 1 : 0 },
                "game.load" => Restore(),
                "game.returnToMenu" => Exit(),
                "clash.invoke" when args.String("action") == "StartMainMenuServerEntryFlow" => Restore(),
                _ => null
            };
            object Restore() { restored = active = true; epoch++; return new { returnValue = true }; }
            object Exit() { active = false; return new { }; }
            var scenario = new SessionPersistenceScenario(f.Run, f.Polling, new(f.Run, f.Polling), NewEntry(f, () => "http://current"));
            Func<Task> action = reconnect ? scenario.Reconnect : scenario.SaveReload;
            bool shouldFail = reconnect ? changed != "none" && changed != "stock" && changed != "pawns" : changed != "none" && changed != "colony";
            if (shouldFail) await Reject<AssertionException>(action); else await action();
            Check(f.Calls.All(c => c.Client == "B"), "Persistence scenario changed client.");
            if (reconnect)
            {
                var actions = f.Calls.Where(c => c.Method == "clash.invoke").Select(c => c.Args.String("action")).ToArray();
                Check(actions.SequenceEqual(new[] { "StartManualSnapshotUpload", "StartMainMenuServerEntryFlow" })
                    && f.Count("game.returnToMenu") == 1 && f.Count("game.load") == 0, "Reconnect bypassed snapshot upload or the session lifecycle.");
            }
            else Check(f.Count("game.save") == 1 && f.Count("game.load") == 1 && f.Count("time.set") == 2,
                "Local roundtrip duplicated save/load or pause preparation.");
        }
    }

    private static JsonElement Log(int sequence, string level, string message) => Wire.Element(new { sequence, level, message });
    private static object Game(string epoch, bool control = true, int maps = 1) => new { epoch, playerHasControl = control, playing = true, mapCount = maps, longEventPending = false };
    private static object Inventory(int silver = 20) => new { maps = new[] {
        new { tileId = 33, items = new Dictionary<string, int> { ["Silver"] = silver }, colonists = new[] { "pawn-1", "pawn-2", "pawn-3" }.Select(handle => new { reference = new { handle } }).ToArray() },
        new { tileId = 44, items = new Dictionary<string, int> { ["Silver"] = 7, ["Steel"] = 5 }, colonists = new[] { new { reference = new { handle = "other-map-pawn" } } } } } };
    private static JsonElement Hello(ClientProfile client, string[] expansions) => Wire.Element(new { culture = client.Culture, uiCulture = client.Culture,
        language = client.Language, mods = expansions.Select(e => new { id = "ludeon.rimworld." + e.ToLowerInvariant() }).Append(new { id = "airs.clashofrim" }).ToArray() });
    private static WorldScenario NewWorld(GameScenarioTestRuntime f, SuiteProfile profile, WorldConfigurationRegistry? registry = null, Distances? distances = null)
        => new(f.Run, f.Polling, profile, registry ?? new(), distances ?? new(), _ => throw new InvalidOperationException("Unexpected persistence probe."));
    private static ServerEntryScenario NewEntry(GameScenarioTestRuntime f, Func<string> address)
        => f.CreateEntry(address, world: NewWorld(f, new(), ReadyWorld()));
    private static WorldConfigurationRegistry ReadyWorld()
    {
        var registry = new WorldConfigurationRegistry();
        registry.Submit("user", new WorldConfigurationDto("world", "user", "colony", DateTimeOffset.UnixEpoch,
            "seed", "0.3", "Normal", "Normal", "Normal", "Normal", "0", ["OutlanderCivil"], [], [], [], [], [], "Cassandra", "Peaceful"));
        var payload = WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(1, "<grid><layers><keys><li>0</li></keys><values><li/></values></layers></grid>",
            "<features/>", "<landmarks/>", WorldTileGeometryBinaryCodec.Encode(new WorldTileGeometryDto([new(0, "Surface", 1, [new(0, 1, 0, 0)])]))!));
        Check(registry.StoreWorldSubstrate("user", "colony", "world", payload).Accepted, "Cannot seed authoritative test world.");
        return registry;
    }
    private sealed class Distances : IWorldTileDistanceCalculator
    {
        internal bool Allow = true;
        internal readonly List<(WorldTileGeometryDistanceSource? Geometry, WorldTileRef From, WorldTileRef To, int Overhead)> Calls = [];
        public int? TryCalculateDistance(WorldTileGeometryDistanceSource? geometry, WorldTileRef fromTile, WorldTileRef toTile, int crossLayerOverheadDistanceTiles)
        {
            Calls.Add((geometry, fromTile, toTile, crossLayerOverheadDistanceTiles));
            return Allow ? toTile.Tile switch { 31 => null, 32 => 1, _ => 2 } : 1;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
