using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ObservationDiplomacyScenario(TestRun run, ScenarioPolling polling,
    ColonyScenarioFixture colony, RemoteMapScenario remote, PlayerScenarioFixture players)
{
    private int probeX, probeZ;
    internal async Task Prepare()
    {
        await colony.PauseWhenIdle("A"); await colony.PauseWhenIdle("B");
        await players.DiscoverBoth();
        var fixture = await run.CallAsync("B", "clash.fixture.observation", new { acknowledgeFixture = true });
        probeX = fixture.Int("x"); probeZ = fixture.Int("z");
    }

    internal async Task Observe(string relation, string mode)
    {
        await players.Refresh("A");
        await players.Observe("A", "rimtest-B", relation: relation);
        CheckRelation(await Read(), relation);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            await remote.Observe(mode, false, async () =>
            {
                CheckMap(await Read(), relation, mode);
                await polling.Step("A", 180);
                CheckMap(await Read(), relation, mode);
                // A refresh must have nothing to correct after opening or ticking.
                await players.Refresh("A");
                CheckMap(await Read(), relation, mode);
            });
            CheckRelation(await Read(), relation);
        }
    }
    private Task<JsonElement> Read() => run.CallAsync("A", "clash.remote.rules", new { userId = "rimtest-B", x = probeX, z = probeZ });
    private static void CheckRelation(JsonElement state, string relation)
    {
        var diplomacy = state.GetProperty("diplomacy");
        Check(diplomacy.String("ownerToPlayer") == relation && diplomacy.String("playerToOwner") == relation,
            "Observation changed the authoritative player relationship: " + state.GetRawText());
    }
    private static void CheckMap(JsonElement state, string relation, string mode)
    {
        CheckRelation(state, relation);
        var residents = state.GetProperty("residents").EnumerateArray().ToArray();
        Check(residents.Length >= 3 && residents.All(p => p.String("playerRelation") == relation
            && !p.Bool("isColonistPlayerControlled") && !p.Bool("enemyIsPlayer")
            && p.String("lord") == "LordJob_DefendBase" && p.OptionalString("duty") is not null),
            "Remote residents lost neutral/allied behavior or native defense duties.");
        var concealment = state.GetProperty("concealment");
        bool scout = mode == "Scout";
        Check(concealment.Int(scout ? "hiddenTraps" : "visibleTraps") >= 1
            && concealment.Int(scout ? "visibleTraps" : "hiddenTraps") == 0,
            "Observation changed trap concealment policy.");
        Check(concealment.Bool("probeHiddenTrap") == scout && concealment.Bool("probeVisibleTrap") != scout,
            "The fixture trap was lost or changed visibility.");
        Check(concealment.Bool("probeFogged") == scout && concealment.Bool("edgeRevealed"),
            "Observation lost sealed-room fog or open-edge visibility.");
    }
}
