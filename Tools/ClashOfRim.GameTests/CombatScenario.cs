using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class CombatScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony, RemoteMapScenario remote, RaidScenario raids)
{
    private JsonElement source, viewerFactions;
    internal async Task Prepare()
    {
        await colony.PauseWhenIdle("A"); await colony.PauseWhenIdle("B");
        await run.CallAsync("B", "clash.fixture.combat", new { acknowledgeFixture = true });
        source = await Read("B"); viewerFactions = (await Read("A")).GetProperty("factions").Clone();
        Check(source.GetProperty("points").GetArrayLength() >= 4, "Missing defense points.");
        foreach (var mode in Modes)
            Check(source.GetProperty("points").EnumerateArray().Single(p => p.OptionalString("pawn") == "RimTestCombat " + mode).String("mode") == mode, "Original mode action or exclusive assignment failed: " + mode);
    }
    internal async Task Observe()
    {
        for (int repeat = 0; repeat < 2; repeat++)
        {
            await remote.ObserveAlly(false, async () =>
            {
                await colony.PauseWhenIdle("A");
                var before = await Read("A", true); Factions(before, false); Npcs(before);
                foreach (var p in Tagged(before).Where(p => !IsNpc(p)))
                    Check(p.String("lord") == "LordJob_DefendBase" && p.OptionalString("duty") is not null, "Friendly remote resident lacks a native duty: " + p.GetRawText());
                await polling.Step("A", 180);
                var after = await Read("A", true); Factions(after, false); Npcs(after);
                Check(Tagged(after).All(p => !p.Bool("dead") && !p.Bool("downed") && p.OptionalString("job") is not null), "Remote simulation left an idle/invalid pawn.");
            });
            Equal(viewerFactions, (await Read("A")).GetProperty("factions"), "Closing observation changed viewer NPC relations");
            Equal(source, await Read("B"), "Remote simulation mutated the source colony");
        }
    }
    internal async Task Raid()
    {
        await raids.RunAndReconnect(false, async eventId =>
        {
            await colony.PauseWhenIdle("A");
            var before = await Read("A", true); Factions(before, true); Npcs(before); Duties(before);
            Check(before.GetProperty("pawns").EnumerateArray().Any(p => p.Bool("player") && p.Bool("isColonistPlayerControlled")), "Attacking player pawn lost control or faction.");
            await polling.Step("A", 300);
            var ticking = await Read("A", true);
            for (int i = 0; i < 8 && (Pawn(ticking, "Hold").GetProperty("distanceToFocus").GetDouble() > 1
                || Pawn(ticking, "OperateEquipment").OptionalString("job") != "ManTurret"); i++)
            {
                await polling.Step("A", 120); ticking = await Read("A", true);
            }
            Factions(ticking, true); Duties(ticking); Npcs(ticking);
            var hold = Pawn(ticking, "Hold");
            Check(hold.GetProperty("distanceToFocus").GetDouble() <= 1 && hold.OptionalString("job") is not null, "Hold pawn did not reach/hold its exact defense point.");
            Check(Pawn(ticking, "OperateEquipment").String("job") == "ManTurret", "Equipment defender did not operate the available mortar.");
            Check(Pawn(ticking, "Defend").GetProperty("distanceToFocus").GetDouble() <= 9, "Defender did not return within its configured radius.");
            Check(Pawn(ticking, "Assault").String("position") != Pawn(before, "Assault").String("position"), "Assault defender did not advance to its duty point.");
            Check(Pawn(ticking, "Raider").String("position") != Pawn(before, "Raider").String("position"), "NPC assault lord did not advance its pawn.");
            int shots = Pawn(ticking, "Hold").Int("shotsFired");
            await run.CallAsync("A", "clash.fixture.combatTarget", new { acknowledgeFixture = true });
            await polling.Step("A", 240);
            var firing = await Read("A", true);
            Check(Pawn(firing, "Hold").Int("shotsFired") > shots && Pawn(firing, "Hold").String("position") == hold.String("position"), "Hold defender did not fire from its assigned point.");
            await remote.ReloadBattle(eventId);
            var loaded = await Read("A", true); Factions(loaded, true); Duties(loaded); Npcs(loaded);
            Equal(ticking.GetProperty("points"), loaded.GetProperty("points"), "Battle reload changed defense modes/assignment/radius");
            await polling.Step("A", 60); Factions(await Read("A", true), true);
        });
        Equal(viewerFactions, (await Read("A")).GetProperty("factions"), "Settling a raid changed viewer NPC relations");
        var restored = await Read("B");
        Equal(source.GetProperty("points"), restored.GetProperty("points"), "Defender settlement lost point configuration or assignment");
        Check(Tagged(restored).Where(p => !IsNpc(p)).All(p => p.Bool("player")), "Defender reload retained remote proxy ownership.");
    }
    private Task<JsonElement> Read(string client, bool isRemote = false) => run.CallAsync(client, "clash.combat", new { remote = isRemote });
    private static readonly string[] Modes = ["Hold", "Defend", "Assault", "OperateEquipment"];
    private static IEnumerable<JsonElement> Tagged(JsonElement map) => map.GetProperty("pawns").EnumerateArray().Where(p => p.OptionalString("name")?.StartsWith("RimTestCombat ", StringComparison.Ordinal) == true);
    private static bool IsNpc(JsonElement pawn) => pawn.String("name") is "RimTestCombat Raider" or "RimTestCombat Visitor";
    private static JsonElement Pawn(JsonElement map, string role) => Tagged(map).Single(p => p.String("name") == "RimTestCombat " + role);
    private void Factions(JsonElement map, bool raid)
    {
        foreach (var p in Tagged(map))
        {
            var original = Tagged(source).Single(s => s.String("name") == p.String("name"));
            Check(!IsNpc(p) || p.OptionalString("factionDef") == original.String("factionDef"), "NPC faction definition was lost/changed: " + p.GetRawText());
            Check(!p.Bool("player") && !p.Bool("isColonistPlayerControlled"), "Projected resident/NPC became player controlled: " + p.GetRawText());
            if (!IsNpc(p)) Check(p.String("faction") == map.String("owner") && p.String("playerRelation") == (raid ? "Hostile" : "Ally"), "Remote resident faction/hostility wrong.");
            else Check(p.String("ownerRelation") == original.String("playerRelation"), "NPC relationship to source owner was lost.");
        }
        Equal(viewerFactions, map.GetProperty("factions"), "Projection changed viewer NPC diplomatic relationships");
    }
    private static void Npcs(JsonElement map)
    {
        Check(Pawn(map, "Raider").String("lord") == "LordJob_AssaultColony" && Pawn(map, "Raider").OptionalString("duty") is not null, "Remote hostile NPC lost assault lord/duty.");
        Check(Pawn(map, "Visitor").String("lord") == "LordJob_DefendBase" && Pawn(map, "Visitor").OptionalString("duty") is not null, "Remote friendly NPC lost defense lord/duty.");
    }
    private static void Duties(JsonElement map)
    {
        foreach (string mode in Modes)
        {
            var pawn = Pawn(map, mode);
            string expected = mode == "OperateEquipment" ? "ClashOfRim_OperateDefenseEquipment" : "ClashOfRim_" + mode + "DefensePoint";
            Check(pawn.String("duty") == expected && !pawn.Bool("dead") && !pawn.Bool("downed"), "Wrong or unusable defense duty: " + pawn.GetRawText());
            var point = map.GetProperty("points").EnumerateArray().Single(p => p.OptionalString("pawn") == pawn.String("name"));
            Check(point.String("position") == pawn.String("focus") && point.String("mode") == mode && point.Int("assignedCount") == 1, "Defense focus or assignment changed.");
        }
        Check(Pawn(map, "Fallback").String("duty") == "ClashOfRim_DefendDefensePoint", "Unassigned resident lost fallback defense.");
        if (Tagged(map).Any(p => p.String("name") == "RimTestCombat Mech")) Check(Pawn(map, "Mech").String("duty") == "ClashOfRim_DefendDefensePoint", "Assigned mech lost defense duty.");
    }
}
