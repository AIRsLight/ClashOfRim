using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ColonyScenarioFixture(TestRun run, ScenarioPolling polling)
{
    internal async Task<JsonElement> Stabilize(string client)
    {
        await run.CallAsync(client, "time.set", new { speed = "Paused" });
        // An authoritative initial snapshot can still contain unopened starting pods.
        // Use the same settling/drafting fixture after entry and snapshot restores.
        await polling.Step(client, 600);
        var home = (await run.CallAsync(client, "clash.inventory")).GetProperty("maps")[0];
        foreach (var pawn in home.GetProperty("colonists").EnumerateArray())
            await run.CallAsync(client, "pawn.draft", new { pawn = H(pawn), drafted = true });
        return home;
    }
    internal async Task<int> Stock(string client, string def)
    {
        var inventory = await run.CallAsync(client, "clash.inventory");
        return inventory.GetProperty("maps").EnumerateArray().Sum(m => m.GetProperty("items").TryGetProperty(def, out var v) ? v.GetInt32() : 0);
    }
    internal async Task Reload(string client, string name)
    {
        await PauseWhenIdle(client);
        await ReloadPaused(client, name);
    }
    internal async Task PauseWhenIdle(string client)
    {
        await polling.Idle(client);
        await run.CallAsync(client, "time.set", new { speed = "Paused" });
    }
    // Callers may capture their comparison baseline after pausing and before saving.
    internal async Task ReloadPaused(string client, string name)
    {
        name += "-" + Guid.NewGuid().ToString("N")[..8];
        string epoch = (await run.CallAsync(client, "game.state")).String("epoch");
        await run.CallAsync(client, "game.save", new { name }); await run.CallAsync(client, "game.load", new { name });
        await polling.Wait(client, "game.state", null, s => s.String("epoch") != epoch && s.Bool("playerHasControl") && s.Int("mapCount") > 0, 180);
        await run.CallAsync(client, "time.set", new { speed = "Paused" });
    }
}
