using System.Text.Json;
using System.Text.Json.Nodes;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class PocketMapScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony, RemoteMapScenario remote, RaidScenario raid)
{
    private JsonElement defenderBefore;
    internal async Task Observe()
    {
        await colony.PauseWhenIdle("A");
        string local = (await Create(null)).String("childMapId");
        await colony.PauseWhenIdle("B");
        string sourceChild = (await run.CallAsync("B", "clash.fixture.pocket", new { acknowledgeFixture = true, stamp = true })).String("childMapId");
        var task = await run.CallAsync("B", "clash.fixture.pocket", new { acknowledgeFixture = true, kind = "quest-hatches", nestedMapId = sourceChild });
        string sourceHatch = task.GetProperty("hatches").EnumerateArray().Single(h => h.String("stockpile") == "Gravcore").String("id");
        await run.CallAsync("B", "clash.fixture.pocket", new { acknowledgeFixture = true, mapId = sourceChild, portalId = sourceHatch, unlock = true, stamp = true });
        // A second genuine quest leaves its real Gravcore hatch unopened.
        await run.CallAsync("B", "clash.fixture.pocket", new { acknowledgeFixture = true, kind = "quest-hatches" });
        defenderBefore = await run.CallAsync("B", "clash.pocket.state");
        await run.CallAsync("A", "clash.fixture.pocket", new { acknowledgeFixture = true, kind = "signal-sentinel" });
        await remote.ObserveAlly(false, async () =>
        {
            await colony.PauseWhenIdle("A");
            string root = (await run.CallAsync("A", "clash.remote")).GetProperty("session").String("clientMapId");
            var state = await Read();
            Check(state.GetProperty("pockets").GetArrayLength() == 3
                && state.GetProperty("pockets").EnumerateArray().Where(p => p.String("mapId") != local).All(p => p.Bool("guarded") && p.GetProperty("pawns").EnumerateArray().All(n => n.Bool("marked"))),
                "Remote child generation lost ownership or cleanup tags.");
            VerifyImported(state);
            var hatch = state.GetProperty("portals").EnumerateArray().First(p => p.String("mapId") == root && p.String("kind") == "AncientHatch" && p.GetProperty("layout").ValueKind == JsonValueKind.String);
            Check(!hatch.Bool("enterable") && hatch.GetProperty("childId").ValueKind == JsonValueKind.Null, "Unopened locked hatch changed on import.");
            await run.CallAsync("A", "clash.fixture.pocket", new { acknowledgeFixture = true, mapId = root, portalId = hatch.String("id"), unlock = true, generate = false, blockedProbe = true });
            string nested = (await Create(root, portalId: hatch.String("id"))).String("childMapId");
            var letters = (await Read()).GetProperty("fixtureLetters").EnumerateArray().Select(l => l.GetString()).ToArray();
            Check(letters.Contains("RimTest pocket signal remote") && !letters.Contains("RimTest pocket signal local"), "Imported signal triggered a local receiver or failed to reach its projected receiver.");
            await run.CallAsync("A", "clash.fixture.pocketDestroy", new { acknowledgeFixture = true, mapId = nested });
            Check((await Read()).GetProperty("pockets").GetArrayLength() == 3, "Native collapse left a child behind.");
            var realHatch = UnopenedRealHatch(state, root);
            string generated = (await run.CallAsync("A", "clash.fixture.pocket", new { acknowledgeFixture = true, mapId = root, portalId = realHatch.String("id"), unlock = true })).String("childMapId");
            Check((await Read()).GetProperty("pockets").EnumerateArray().Single(p => p.String("mapId") == generated).GetProperty("gravcores").GetInt32() > 0,
                "Independent real stockpile generation lost its Gravcore objective.");
            string child = state.GetProperty("pockets").EnumerateArray().Single(p => p.String("sourceMapId") == root).String("mapId");
            var gate = state.GetProperty("portals").EnumerateArray().Single(p => p.String("mapId") == child && p.String("kind") == "AncientHatch");
            await Create(gate.String("childId")); // independently generate a remote pit beneath the imported vault
        }, existingPocketCount: 2);
        var closed = await Read();
        Check(closed.GetProperty("pockets").EnumerateArray().Single().String("mapId") == local
            && closed.GetProperty("temporaryWorldPawns").GetArrayLength() == 0, "Observation close leaked pockets/pawns or removed local pockets.");
        await run.CallAsync("A", "clash.fixture.pocketDestroy", new { acknowledgeFixture = true, mapId = local });
        VerifyDefender(await run.CallAsync("B", "clash.pocket.state"));
    }
    internal async Task Raid()
    {
        await raid.RunAndReconnect(false, async eventId =>
        {
            await colony.PauseWhenIdle("A");
            string root = (await run.CallAsync("A", "clash.remote")).GetProperty("session").String("clientMapId");
            var state = await Read(); VerifyImported(state);
            string pawn = state.GetProperty("players").EnumerateArray().Single(p => p.String("mapId") == root).String("id");
            string portal = state.GetProperty("portals").EnumerateArray().Single(p => p.String("mapId") == root && p.String("kind") == "PitGate").String("id");
            await Enter(portal, pawn, id => id != root);
            state = await Read(); string child = state.GetProperty("players").EnumerateArray().Single(p => p.String("id") == pawn).String("mapId");
            Check(!state.Bool("removable"), "Battle became removable while its attacker was in a child map.");
            Check(!state.GetProperty("pockets").EnumerateArray().SelectMany(p => p.GetProperty("pawns").EnumerateArray()).Single(p => p.String("id") == pawn).Bool("marked"),
                "Native portal entry tagged the real attacker as a disposable projection.");
            string exit = state.GetProperty("portals").EnumerateArray().Single(p => p.String("id") == portal).String("exitId");
            await Enter(exit, pawn, id => id == root);
            await Enter(portal, pawn, id => id == child);
            var nestedGate = state.GetProperty("portals").EnumerateArray().Single(p => p.String("mapId") == child && p.String("kind") == "AncientHatch");
            await Enter(nestedGate.String("id"), pawn, id => id == nestedGate.String("childId"));
            await Enter(nestedGate.String("exitId"), pawn, id => id == child);
            await Enter(exit, pawn, id => id == root);
            var unopened = UnopenedRealHatch(state, root);
            await run.CallAsync("A", "clash.fixture.pocket", new { acknowledgeFixture = true, mapId = root, portalId = unopened.String("id"), unlock = true, generate = false });
            await Enter(unopened.String("id"), pawn, id => id != root);
            state = await Read(); child = state.GetProperty("players").EnumerateArray().Single(p => p.String("id") == pawn).String("mapId");
            var corpsesBefore = CorpseState(state);
            await remote.ReloadBattle(eventId);
            state = await Read();
            Check(corpsesBefore.Length > 0 && corpsesBefore.SequenceEqual(CorpseState(state)), "Reload lost corpses or their referenced dead pawns.");
            Check(state.GetProperty("players").EnumerateArray().Single(p => p.String("id") == pawn).String("mapId") == child
                && !state.Bool("removable") && state.GetProperty("pockets").EnumerateArray().All(p => p.Bool("guarded")),
                "Native reload lost child-map pawn placement or battle ownership.");
            Check(!state.GetProperty("pockets").EnumerateArray().SelectMany(p => p.GetProperty("pawns").EnumerateArray()).Single(p => p.String("id") == pawn).Bool("marked"),
                "Reload tagged the real attacker as a disposable projection.");
            VerifyLayouts(state);
            Check(state.GetProperty("pockets").EnumerateArray().Single(p => p.String("mapId") == child).GetProperty("gravcores").GetInt32() > 0,
                "Reload lost independently generated stockpile contents.");
            // Finish from inside the pocket to exercise client close and server loss accounting.
        });
        var closed = await Read();
        Check(closed.GetProperty("pockets").GetArrayLength() == 0 && closed.GetProperty("temporaryWorldPawns").GetArrayLength() == 0,
            "Settlement left child maps or temporary world pawns registered.");
        VerifyDefender(await run.CallAsync("B", "clash.pocket.state"));
    }
    private void VerifyImported(JsonElement state)
    {
        var remotePockets = state.GetProperty("pockets").EnumerateArray().Where(p => p.Bool("guarded")).ToArray();
        Check(remotePockets.Length == 2, "Existing defender subtree was not imported.");
        Check(remotePockets.All(p => !p.GetProperty("pawns").EnumerateArray().Any(n => n.Bool("player"))),
            "Existing pocket import unexpectedly required attacker entry.");
        var before = defenderBefore.GetProperty("pockets").EnumerateArray().SelectMany(p => p.GetProperty("markers").EnumerateArray()).Select(m => m.GetRawText()).Order().ToArray();
        var after = remotePockets.SelectMany(p => p.GetProperty("markers").EnumerateArray()).Select(m => m.GetRawText()).Order().ToArray();
        Check(before.SequenceEqual(after) && after.Length == 2, "Imported maps regenerated or lost their distinctive source contents.");
        foreach (var pocket in remotePockets)
            Check(state.GetProperty("portals").EnumerateArray().Count(p => p.GetProperty("childId").GetString() == pocket.String("mapId") && p.GetProperty("exitId").ValueKind == JsonValueKind.String) == 1,
                "Imported child lost its unique native entrance/exit connection.");
        VerifyLayouts(state);
    }
    private static JsonElement UnopenedRealHatch(JsonElement state, string root) => state.GetProperty("portals").EnumerateArray()
        .Single(p => p.String("mapId") == root && p.String("kind") == "AncientHatch" && p.GetProperty("layout").ValueKind == JsonValueKind.Null);
    private static void VerifyLayouts(JsonElement state)
    {
        var layouts = state.GetProperty("pockets").EnumerateArray().SelectMany(p => p.GetProperty("layouts").EnumerateArray()).ToArray();
        var ids = layouts.Select(l => l.String("id")).Concat(layouts.SelectMany(l => l.GetProperty("rooms").EnumerateArray().Select(r => r.GetString()))).ToArray();
        Check(layouts.All(l => l.Bool("valid")) && ids.Distinct().Count() == ids.Length, "Room/layout links are invalid or duplicate IDs survived projection/reload.");
        Check(state.GetProperty("pockets").EnumerateArray().SelectMany(p => p.GetProperty("corpses").EnumerateArray()).All(c => c.Bool("valid")), "Pocket map contains a corpse without its pawn.");
    }
    private static string[] CorpseState(JsonElement state) => state.GetProperty("pockets").EnumerateArray()
        .SelectMany(p => p.GetProperty("corpses").EnumerateArray()).Select(c => c.GetRawText()).Order().ToArray();
    private void VerifyDefender(JsonElement after)
    {
        var expectedPockets = JsonNode.Parse(defenderBefore.GetProperty("pockets").GetRawText())!.AsArray();
        // Vanilla upgrades a source LayoutRoom ID of zero to a random ID on
        // ordinary load. Allow only that migration, retaining every other field.
        for (int m = 0; m < expectedPockets.Count; m++)
        {
            var layouts = expectedPockets[m]!["layouts"]!.AsArray();
            for (int l = 0; l < layouts.Count; l++)
            {
                var rooms = layouts[l]!["rooms"]!.AsArray();
                for (int r = 0; r < rooms.Count; r++)
                    if (rooms[r]!.GetValue<string>() == "LayoutRoom_0")
                        rooms[r] = after.GetProperty("pockets")[m].GetProperty("layouts")[l].GetProperty("rooms")[r].GetString();
            }
        }
        Equal(JsonSerializer.SerializeToElement(expectedPockets), after.GetProperty("pockets"), "Attacker changed defender pockets");
        foreach (string field in new[] { "quests", "fixtureLetters" }) Equal(defenderBefore.GetProperty(field), after.GetProperty(field), "Attacker changed defender " + field);
        foreach (var portal in defenderBefore.GetProperty("portals").EnumerateArray())
        {
            var current = after.GetProperty("portals").EnumerateArray().Single(p => p.String("id") == portal.String("id"));
            foreach (string field in new[] { "childId", "exitId", "hacked" }) Equal(portal.GetProperty(field), current.GetProperty(field), "Attacker changed defender entrance " + field);
        }
    }
    private Task<JsonElement> Read() => run.CallAsync("A", "clash.pocket.state");
    private Task<JsonElement> Create(string? mapId, bool generate = true, string? portalId = null)
        => run.CallAsync("A", "clash.fixture.pocket", new { acknowledgeFixture = true, mapId, generate, portalId });
    private async Task Enter(string portalId, string pawnId, Func<string, bool> arrived)
    {
        var before = await Read();
        var portal = before.GetProperty("portals").EnumerateArray().Single(p => p.String("id") == portalId);
        // Keep the single test traveler alive; combat AI is covered separately.
        // This does not move the pawn or replace the native portal job.
        await run.CallAsync("A", "clash.fixture.pocket", new { acknowledgeFixture = true, kind = "travel-control", mapId = portal.String("mapId") });
        string pawnHandle = H(before.GetProperty("players").EnumerateArray().Single(p => p.String("id") == pawnId)), target = H(portal);
        await run.CallAsync("A", "pawn.draft", new { pawn = pawnHandle, drafted = true });
        var options = Items(await run.CallAsync("A", "orders.options", new { pawn = pawnHandle, target }));
        var option = options.Single(o => o.String("label") == portal.String("enterLabel") && !o.Bool("disabled"));
        var accepted = await run.CallAsync("A", "orders.choose", new { pawn = pawnHandle, target, id = option.String("id") });
        Check(accepted.GetProperty("job").String("defName") == "EnterPortal" && accepted.GetProperty("job").Bool("playerForced"), "Native portal menu did not order its forced job.");
        for (int i = 0; i < 40; i++)
        {
            await polling.Step("A", 120);
            var state = await Read();
            var pawn = state.GetProperty("players").EnumerateArray().SingleOrDefault(p => p.String("id") == pawnId);
            if (pawn.ValueKind != JsonValueKind.Undefined && arrived(pawn.String("mapId"))) return;
        }
        throw new Exception("Pawn never completed native portal entry: " + (await run.CallAsync("A", "pawn.state", new { pawn = pawnHandle })).GetRawText());
    }
}
