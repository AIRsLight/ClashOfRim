using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class FeatureRefreshScenario(TestRun run, EconomicScenarioFixture economy)
{
    internal async Task Run(IEnumerable<string> clients)
    {
        foreach (string client in clients)
        {
            if (!(await run.CallAsync(client, "clash.state")).Bool("isInActiveMultiplayerSession")) continue;
            await economy.RefreshBank(client);
            foreach (string action in new[] { "StartRefreshAchievements", "StartRefreshTradeOrders", "StartRefreshServerShop", "StartSyncWorldMapMarkers" })
                await run.InvokeClientAsync(client, action);
            await run.CallAsync(client, "clash.records", new { kind = "markers" });
        }
    }
}
