using System.Text.Json;
using AIRsLight.ClashOfRim.Protocol;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class DiplomacyScenario(TestRun run, ScenarioPolling polling, PlayerScenarioFixture players)
{
    internal async Task Alliance()
    {
        await polling.Idle("A");
        string actorColony = (await run.CallAsync("A", "clash.state")).String("colonyId");
        string targetColony = (await run.CallAsync("B", "clash.state")).String("colonyId");
        await players.Refresh("A");
        var target = await players.Observe("A", "rimtest-B", targetColony);
        var previous = Items(await run.CallAsync("B", "clash.events", new { limit = 200 }))
            .Select(v => v.GetProperty("data").String("eventId")).ToHashSet(StringComparer.Ordinal);
        await run.InvokeClientAsync("A", "StartCreateDiplomacyEvent", new { kind = "AllianceRequest", target = target.GetProperty("reference"), customMessage = "RimTest alliance" });
        await polling.Idle("A");
        bool CurrentRequest(JsonElement value) => !previous.Contains(value.GetProperty("data").String("eventId")) && IsAllianceRequest(value, actorColony, targetColony);
        var events = await polling.Wait("B", "clash.events", new { limit = 200 }, p => Items(p).Any(CurrentRequest));
        var request = Items(events).Single(CurrentRequest);
        await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = request.GetProperty("data").String("eventId"), actionKind = "Accept" });
        await polling.Idle("B");
        await players.Refresh("A");
        await players.Observe("A", "rimtest-B", targetColony, "Ally");
    }
    internal static bool IsAllianceRequest(JsonElement entry, string actorColony, string targetColony)
    {
        var data = entry.GetProperty("data");
        return data.GetProperty("eventType").Deserialize<ServerEventType>(Wire.Json) == ServerEventType.AllianceRequest
            && data.GetProperty("actor").String("userId") == "rimtest-A" && data.GetProperty("actor").String("colonyId") == actorColony
            && data.GetProperty("target").String("userId") == "rimtest-B" && data.GetProperty("target").String("colonyId") == targetColony;
    }
}
