using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class GravshipScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs, ColonyMoveScenarioFixture moves)
{
    internal async Task Run(string client, bool existingSite)
    {
        var before = await moves.Prepare(client);
        var fixture = await run.CallAsync(client, "clash.fixture.gravship", new { site = existingSite, acknowledgeFixture = true });
        await polling.Step(client, 30);
        await run.CallAsync(client, "clash.gravship.pilot", new { pawn = fixture.GetProperty("pawn").String("handle"), console = fixture.GetProperty("console").String("handle") });
        await polling.RunUntil(client, "clash.gravship", null, s => s.Bool("ritualDialog"), 120);
        await run.CallAsync(client, "clash.gravship.begin"); await dialogs.Confirm(client);
        await polling.RunUntil(client, "clash.gravship", null, s => s.Bool("choosingTile"), 120);
        // The engine owns the cutscene clock after destination selection.
        await run.CallAsync(client, "time.set", new { speed = "Normal" });
        await run.CallAsync(client, "clash.gravship.destination", new { tile = fixture.Int("tile") }); await dialogs.Confirm(client);
        var arrival = await polling.Wait(client, "clash.gravship", null, s => s.Bool("landing"), 180);
        if (arrival.GetProperty("markers").GetArrayLength() == 0) await run.CallAsync(client, "clash.gravship.placeLanding");
        await polling.Wait(client, "clash.gravship", null, s => s.GetProperty("markers").GetArrayLength() == 1);
        await run.CallAsync(client, "clash.gravship.land");
        await moves.AwaitCommit(before); await run.CallAsync(client, "time.set", new { speed = "Paused" });
        await moves.VerifyDestination(before, fixture.Int("tile"), fixture.GetProperty("pawns"), before.Steel);
        var engines = (await run.CallAsync(client, "clash.gravship")).GetProperty("engines");
        Check(engines.GetArrayLength() == 1 && engines[0].String("id") == fixture.String("engine") && engines[0].GetProperty("fuel").GetSingle() < 100,
            "Flight failed to transport original engine or consume fuel.");
        await moves.ReloadAndRepay(before, fixture.Int("tile"), fixture.GetProperty("pawns"), before.Steel, "rimtest-gravship");
    }
}
