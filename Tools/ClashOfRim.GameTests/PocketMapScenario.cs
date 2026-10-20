using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class PocketMapScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony, RemoteMapScenario remote, RaidScenario raid)
{
    internal async Task Observe()
    {
        await colony.PauseWhenIdle("A");
        string local = (await Create(null)).String("childMapId");
        await colony.PauseWhenIdle("B");
        await run.CallAsync("B", "clash.fixture.pocket", new { acknowledgeFixture = true, generate = false });
        await remote.ObserveAlly(false, async () =>
        {
            await colony.PauseWhenIdle("A");
            string root = (await run.CallAsync("A", "clash.remote")).GetProperty("session").String("clientMapId");
            string projectedGate = (await Read()).GetProperty("portals").EnumerateArray().Single(p => p.String("mapId") == root).String("id");
            string child = (await Create(root, portalId: projectedGate)).String("childMapId");
            string nested = (await Create(child)).String("childMapId");
            var state = await Read();
            Check(state.GetProperty("pockets").GetArrayLength() == 3
                && state.GetProperty("pockets").EnumerateArray().Where(p => p.String("mapId") != local).All(p => p.Bool("guarded") && p.GetProperty("pawns").EnumerateArray().All(n => n.Bool("marked"))),
                "Remote child generation lost ownership or cleanup tags.");
            await run.CallAsync("A", "clash.fixture.pocketDestroy", new { acknowledgeFixture = true, mapId = nested });
            Check((await Read()).GetProperty("pockets").GetArrayLength() == 2, "Native collapse left a child behind.");
            await Create(child); // close the owning session while viewing its deepest descendant
        });
        var closed = await Read();
        Check(closed.GetProperty("pockets").EnumerateArray().Single().String("mapId") == local
            && closed.GetProperty("temporaryWorldPawns").GetArrayLength() == 0, "Observation close leaked pockets/pawns or removed local pockets.");
        await run.CallAsync("A", "clash.fixture.pocketDestroy", new { acknowledgeFixture = true, mapId = local });
    }
    internal async Task Raid()
    {
        await raid.RunAndReconnect(false, async eventId =>
        {
            await colony.PauseWhenIdle("A");
            string root = (await run.CallAsync("A", "clash.remote")).GetProperty("session").String("clientMapId");
            var entrance = await Create(root, false); string pawn = entrance.String("pawnId"), portal = entrance.String("portalId");
            await Enter(portal, pawn, id => id != root);
            var state = await Read(); string child = state.GetProperty("pockets").EnumerateArray().Single().String("mapId");
            Check(!state.Bool("removable"), "Battle became removable while its attacker was in a child map.");
            Check(!state.GetProperty("pockets")[0].GetProperty("pawns").EnumerateArray().Single(p => p.String("id") == pawn).Bool("marked"),
                "Native portal entry tagged the real attacker as a disposable projection.");
            string exit = state.GetProperty("portals").EnumerateArray().Single(p => p.String("id") == portal).String("exitId");
            await Enter(exit, pawn, id => id == root);
            await Enter(portal, pawn, id => id == child);
            await remote.ReloadBattle(eventId);
            state = await Read();
            Check(state.GetProperty("players").EnumerateArray().Single(p => p.String("id") == pawn).String("mapId") == child
                && !state.Bool("removable") && state.GetProperty("pockets").EnumerateArray().Single().Bool("guarded"),
                "Native reload lost child-map pawn placement or battle ownership.");
            Check(!state.GetProperty("pockets")[0].GetProperty("pawns").EnumerateArray().Single(p => p.String("id") == pawn).Bool("marked"),
                "Reload tagged the real attacker as a disposable projection.");
            // Finish from inside the pocket to exercise client close and server loss accounting.
        });
        var closed = await Read();
        Check(closed.GetProperty("pockets").GetArrayLength() == 0 && closed.GetProperty("temporaryWorldPawns").GetArrayLength() == 0,
            "Settlement left child maps or temporary world pawns registered.");
    }
    private Task<JsonElement> Read() => run.CallAsync("A", "clash.pocket.state");
    private Task<JsonElement> Create(string? mapId, bool generate = true, string? portalId = null)
        => run.CallAsync("A", "clash.fixture.pocket", new { acknowledgeFixture = true, mapId, generate, portalId });
    private async Task Enter(string portalId, string pawnId, Func<string, bool> arrived)
    {
        var before = await Read();
        var portal = before.GetProperty("portals").EnumerateArray().Single(p => p.String("id") == portalId);
        string pawnHandle = H(before.GetProperty("players").EnumerateArray().Single(p => p.String("id") == pawnId)), target = H(portal);
        await run.CallAsync("A", "pawn.draft", new { pawn = pawnHandle, drafted = true });
        var options = Items(await run.CallAsync("A", "orders.options", new { pawn = pawnHandle, target }));
        var option = options.Single(o => o.String("label") == portal.String("enterLabel") && !o.Bool("disabled"));
        var accepted = await run.CallAsync("A", "orders.choose", new { pawn = pawnHandle, target, id = option.String("id") });
        Check(accepted.GetProperty("job").String("defName") == "EnterPortal" && accepted.GetProperty("job").Bool("playerForced"), "Native portal menu did not order its forced job.");
        for (int i = 0; i < 15; i++)
        {
            await polling.Step("A", 120);
            var state = await Read();
            var pawn = state.GetProperty("players").EnumerateArray().SingleOrDefault(p => p.String("id") == pawnId);
            if (pawn.ValueKind != JsonValueKind.Undefined && arrived(pawn.String("mapId"))) return;
        }
        throw new Exception("Pawn never completed native portal entry: " + (await run.CallAsync("A", "pawn.state", new { pawn = pawnHandle })).GetRawText());
    }
}
