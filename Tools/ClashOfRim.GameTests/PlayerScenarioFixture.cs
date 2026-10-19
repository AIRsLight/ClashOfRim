using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class PlayerScenarioFixture(TestRun run, ScenarioPolling polling)
{
    internal async Task Refresh(string client)
    {
        await run.InvokeClientAsync(client, "StartRefreshPlayers"); await polling.Idle(client);
    }
    internal async Task<JsonElement> Observe(string client, string userId, string? colonyId = null, string? relation = null)
    {
        bool Matches(JsonElement entry)
        {
            var data = entry.GetProperty("data");
            return data.String("userId") == userId && (colonyId is null || data.String("colonyId") == colonyId)
                && (relation is null || data.OptionalString("relationKind") == relation);
        }
        var players = await polling.Wait(client, "clash.records", new { kind = "players" }, p => Items(p).Any(Matches));
        return Items(players).Single(Matches);
    }
}
