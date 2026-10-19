using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class EconomicScenarioFixture(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colonyFixture)
{
    internal async Task<JsonElement> RefreshBank(string client)
    {
        await polling.Idle(client); await run.InvokeClientAsync(client, "StartRefreshBankStatus"); await polling.Idle(client);
        return await run.CallAsync(client, "clash.bank");
    }
    internal async Task AssertNoMutation(string client, Func<Task> action)
    {
        await polling.Idle(client);
        int silver = await colonyFixture.Stock(client, "Silver"), steel = await colonyFixture.Stock(client, "Steel");
        string snapshot = (await run.CallAsync(client, "clash.state")).String("currentSnapshotId");
        await action(); await polling.Idle(client);
        Check(await colonyFixture.Stock(client, "Silver") == silver && await colonyFixture.Stock(client, "Steel") == steel, "Rejected operation changed stock.");
        Check((await run.CallAsync(client, "clash.state")).String("currentSnapshotId") == snapshot, "Rejected operation published a snapshot.");
    }
    internal async Task AssertSnapshotAdvanced(string client, string previous)
    {
        string snapshot = (await run.CallAsync(client, "clash.state")).String("currentSnapshotId");
        Check(!string.IsNullOrWhiteSpace(snapshot) && snapshot != previous, "Economic operation did not publish a new snapshot.");
    }
}
