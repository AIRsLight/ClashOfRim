using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class RelocationScenario(TestRun run, ColonyMoveScenarioFixture moves)
{
    internal async Task Run(string client, string kind)
    {
        var before = await moves.Prepare(client);
        var fixture = await run.CallAsync(client, "clash.fixture.relocation", new { kind, acknowledgeFixture = true });
        int tile = fixture.Int("tile"); string target = fixture.GetProperty("target").String("handle");
        if (kind == "site")
        {
            var map = await ObserveMap(client, tile);
            Check(map.String("parent") == "Site" && !map.Bool("home"), "Fixture must be a genuine unsettled quest Site.");
            var enemy = await run.CallAsync(client, "clash.fixture.relocationEnemy", new { map = target, acknowledgeFixture = true });
            var threatened = await ObserveMap(client, tile);
            Check(threatened.GetProperty("settle").Bool("disabled"), "Relocation bypassed the site's hostile threat check.");
            await run.CallAsync(client, "clash.fixture.relocationEnemy", new { map = target, remove = enemy.String("id"), acknowledgeFixture = true });
            map = await ObserveMap(client, tile);
            Check(!map.GetProperty("settle").Bool("disabled"), "Existing quest-map relocation is disabled: " + map.GetProperty("settle").GetRawText());
            await run.CallAsync(client, "clash.relocation.settleMap", new { map = target });
        }
        else
        {
            await run.CallAsync(client, "world.select", new { target });
            string label = (await run.CallAsync(client, "clash.relocation")).String("label");
            var command = Items(await run.CallAsync(client, "world.gizmos", new { target })).Single(c => c.String("label") == label);
            Check(!command.Bool("disabled"), "Caravan relocation disabled: " + command.GetRawText());
            await run.CallAsync(client, "world.invokeGizmo", new { target, type = command.String("type"), label = command.String("label") });
        }
        await run.CallAsync(client, "clash.relocation.confirm");
        await moves.AwaitCommit(before);
        await moves.VerifyDestination(before, tile, fixture.GetProperty("pawns"), 10);
        await moves.ReloadAndRepay(before, tile, fixture.GetProperty("pawns"), 10, "rimtest-relocation-" + kind);
    }
    private async Task<JsonElement> ObserveMap(string client, int tile) =>
        (await run.CallAsync(client, "clash.relocation")).GetProperty("maps").EnumerateArray().Single(m => m.Int("tile") == tile);
}
