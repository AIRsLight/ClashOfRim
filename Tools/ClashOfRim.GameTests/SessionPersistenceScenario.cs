using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class SessionPersistenceScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony, ServerEntryScenario entry)
{
    internal async Task SaveReload()
    {
        await colony.PauseWhenIdle("B");
        var before = await run.CallAsync("B", "clash.world.details"); int silver = await colony.Stock("B", "Silver");
        await colony.ReloadPaused("B", "rimtest-roundtrip");
        var after = await run.CallAsync("B", "clash.world.details");
        foreach (string key in new[] { "worldConfigurationId", "info", "layers", "storyteller", "difficulty", "difficultyValues", "features", "factions", "ideologies", "pawns" }) Equal(before.GetProperty(key), after.GetProperty(key), "Save/load changed " + key);
        Check(await colony.Stock("B", "Silver") == silver, "Save/load changed stock.");
    }
    internal async Task Reconnect()
    {
        await polling.Idle("B");
        var before = await run.CallAsync("B", "clash.state"); var world = await run.CallAsync("B", "clash.world.details");
        await run.InvokeClientAsync("B", "StartManualSnapshotUpload"); await polling.Idle("B");
        await entry.ExitSession("B");
        await entry.LoginExisting(true);
        Check((await run.CallAsync("B", "clash.state")).String("colonyId") == before.String("colonyId"), "Reconnect changed colony identity.");
        var after = await run.CallAsync("B", "clash.world.details");
        foreach (string key in new[] { "worldConfigurationId", "info", "layers", "storyteller", "difficulty", "difficultyValues", "features", "factions", "ideologies" }) Equal(world.GetProperty(key), after.GetProperty(key), "Reconnect changed " + key);
    }
}
