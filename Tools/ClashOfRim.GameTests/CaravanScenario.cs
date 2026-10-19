using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class CaravanScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs, ColonyScenarioFixture colonyFixture)
{
    private string? expedition;
    internal string Handle => expedition ?? throw new InvalidOperationException("Missing expedition.");
    internal void Use(JsonElement caravan) => expedition = H(caravan);
    internal async Task RefreshSinglePawnCaravan()
        => Use(Items(await run.CallAsync("A", "caravans.list")).Single(c => c.GetProperty("pawns").GetArrayLength() == 1));
    internal Task Travel() => Travel(null);
    internal async Task Travel(string? selectedPawn)
    {
        await colonyFixture.StoreStartingResources("A");
        await polling.Idle("A"); string map = H(Items(await run.CallAsync("A", "game.maps"))[0]);
        var pawns = Items(await run.CallAsync("A", "pawns.list", new { map })).Where(p => p.Bool("playerControlled")).ToArray();
        string pawn = selectedPawn ?? H(pawns[0]);
        Check(pawns.Any(p => H(p) == pawn), "Selected transfer pawn is not a controlled home pawn.");
        // Economic assertions have finished; allow the home colonists to eat while the expedition travels.
        foreach (var resident in pawns) await run.CallAsync("A", "pawn.draft", new { pawn = H(resident), drafted = false });
        var neighbor = (await run.CallAsync("A", "world.neighbors", new { map })).GetProperty("neighbors").EnumerateArray().First(n => n.Bool("passable"));
        string draft = H(await run.CallAsync("A", "caravan.prepare", new { map, destination = neighbor.GetProperty("tile").Int("tileId") }));
        var groups = Items(await run.CallAsync("A", "caravan.items", new { draft, limit = 200 }));
        var person = groups.Single(g => g.GetProperty("things").EnumerateArray().Any(t => t.String("handle") == pawn));
        await run.CallAsync("A", "caravan.select", new { draft, group = H(person), count = 1 });
        foreach (var (def, count) in new[] { ("WoodLog", 10), ("MealSurvivalPack", 10) })
        {
            var candidates = groups.Where(g => g.String("defName") == def && g.Int("maximum") >= count).ToArray();
            Check(candidates.Length > 0, $"Caravan fixture cannot select {count} {def}; colony stock is {await colonyFixture.Stock("A", def)}. Check home area, reachability and reservations.");
            var group = candidates[0];
            await run.CallAsync("A", "caravan.select", new { draft, group = H(group), count });
        }
        await run.CallAsync("A", "caravan.submit", new { draft }); await dialogs.Confirm("A");
        await run.CallAsync("A", "time.run", new { tps = 2400 });
        var caravans = await polling.Wait("A", "caravans.list", null, p => Items(p).Any(c => c.GetProperty("pawns").EnumerateArray().Any(t => t.String("handle") == pawn)), 180);
        await run.CallAsync("A", "time.cancel");
        expedition = H(Items(caravans).Single(c => c.GetProperty("pawns").EnumerateArray().Any(t => t.String("handle") == pawn)));
        var destination = (await run.CallAsync("B", "clash.inventory")).GetProperty("maps")[0];
        Check((await run.CallAsync("A", "caravan.move", new { caravan = expedition, tileId = destination.Int("tileId"), layerId = destination.Int("layerId") })).Bool("accepted"), "Travel path rejected.");
        await run.CallAsync("A", "time.run", new { tps = 2400 });
        await polling.Wait("A", "caravan.state", new { caravan = expedition }, s => !s.Bool("moving") && s.GetProperty("tile").Int("tileId") == destination.Int("tileId"), 180);
        await run.CallAsync("A", "time.cancel"); await polling.Idle("A");
    }
    internal async Task<int> Stock(string def) => (await run.CallAsync("A", "caravan.state", new { caravan = Handle }))
        .GetProperty("items").EnumerateArray().Where(t => t.String("defName") == def).Sum(t => t.Int("count"));

}
