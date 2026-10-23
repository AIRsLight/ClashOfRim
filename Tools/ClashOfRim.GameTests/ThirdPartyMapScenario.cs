using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ThirdPartyMapScenario(TestRun run, ColonyScenarioFixture colony, RemoteMapScenario remote, RaidScenario raid, Func<string> serverUrl)
{
    private JsonElement source;
    internal async Task Prepare()
    {
        using var http = new HttpClient();
        using var plugins = JsonDocument.Parse(await http.GetStringAsync(serverUrl() + "/server/plugins"));
        var ids = plugins.RootElement.GetProperty("plugins").EnumerateArray().Select(p => p.String("id")).ToHashSet();
        Check(ids.Contains("AIRsLight.ClashOfRim.AdaptiveStorage") && ids.Contains("AIRsLight.ClashOfRim.VehicleFramework"), "Required server compatibility plugins were not loaded.");
        await run.RecordAsync(new { serverObservation = "third-party-plugins", plugins = plugins.RootElement });
        await colony.PauseWhenIdle("B"); await run.CallAsync("B", "clash.fixture.thirdPartyMap", new { acknowledgeFixture = true });
        source = await Read("B");
        Check(source.Int("animations") == 1, "Native duel did not start.");
        Check(source.GetProperty("storage").GetArrayLength() == 1 && source.GetProperty("storage")[0].GetProperty("contents").GetArrayLength() == 4, "Packed storage fixture must contain four distinct stacks in one cell.");
        Check(source.GetProperty("vehicles")[0].GetProperty("passengers").GetArrayLength() == 1 && source.GetProperty("vehicles")[0].Int("cargo") == 17, "Vehicle fixture lost its passenger or cargo.");
    }
    internal async Task Observe()
    {
        await remote.ObserveAlly(false, async () =>
        {
            await colony.PauseWhenIdle("A"); var view = await Read("A", true);
            Check(view.Int("animations") == 0, "Remote projection retained another client's active melee renderer.");
            Storage(view);
            Check(view.GetProperty("vehicles")[0].GetProperty("passengers").GetArrayLength() >= 1, "Remote projection lost native vehicle passengers.");
        });
        Equal(source, await Read("B"), "Projection modified source containers, passengers or native animation.");
        await run.CallAsync("B", "clash.fixture.stopThirdPartyAnimation", new { acknowledgeFixture = true });
    }
    internal async Task Raid()
    {
        await raid.RunAndReconnect(false, async _ =>
        {
            await colony.PauseWhenIdle("A"); var view = await Read("A", true); Storage(view);
            var vehicle = view.GetProperty("vehicles").EnumerateArray().Single(); var crew = vehicle.GetProperty("passengers").EnumerateArray().ToArray();
            Check(crew.Length == 2 && crew.Any(p => p.String("name") == "RimTestVehicle assigned"), "Remote defense point did not board its assigned pawn.");
            Check(!vehicle.Bool("player") && crew.All(p => !p.Bool("player") && !p.Bool("spawned") && p.String("faction") == vehicle.String("faction")), "Defender vehicle crew retained player control or the wrong faction.");
            Check(vehicle.Int("cargo") == 17, "Vehicle cargo changed on battle entry.");
            Check(vehicle.GetProperty("lord").ValueKind == JsonValueKind.Null && vehicle.GetProperty("duty").ValueKind == JsonValueKind.Null, "Vehicle inherited ordinary pawn defense AI.");
        });
        var restored = await Read("B"); Check(restored.GetProperty("vehicles").GetArrayLength() == 1 && restored.GetProperty("vehicles")[0].Int("cargo") == 17, "Settlement lost or duplicated the intact vehicle/cargo.");
    }
    private void Storage(JsonElement view)
    {
        var storage = view.GetProperty("storage").EnumerateArray().Single();
        var contents = storage.GetProperty("contents").EnumerateArray().ToArray();
        Check(contents.Length == 4 && contents.Sum(t => t.Int("stackCount")) == 300 && contents.All(t => t.Bool("spawned") && t.Bool("atContainer") && t.Bool("forbidden")), "Remote adaptive storage lost, moved or exposed packed contents.");
        Check(storage.Int("cellLimit") == source.GetProperty("storage")[0].Int("cellLimit"), "Remote adaptive storage retained an artificial cell limit after loading.");
    }
    private async Task<JsonElement> Read(string client, bool isRemote = false)
    {
        string? mapId = isRemote ? (await run.CallAsync(client, "clash.remote")).GetProperty("session").String("clientMapId") : null;
        return await run.CallAsync(client, "clash.thirdParty.map", new { mapId });
    }
}
