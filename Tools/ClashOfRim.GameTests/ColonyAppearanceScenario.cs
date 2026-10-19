using System.Diagnostics;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ColonyAppearanceScenario(TestRun run, ScenarioPolling polling, WorldConfigurationRegistry world)
{
    internal async Task Run()
    {
        string colony = (await run.CallAsync("A", "clash.state")).String("colonyId");
        var original = Read(colony);
        var changed = new ColonyAppearanceDto("Color", colorHex: "38A4E8");
        try
        {
            await Save(changed);
            await AwaitAppearance(colony, changed);
            await run.InvokeClientAsync("B", "StartSyncWorldMapMarkers");
            await polling.Wait("B", "clash.records", new { kind = "markers" }, p => Items(p).Any(v =>
            {
                var data = v.GetProperty("data");
                return data.OptionalString("ownerUserId") == "rimtest-A" && data.OptionalString("ownerColonyId") == colony
                    && Matches(data.GetProperty("appearance").Deserialize<ColonyAppearanceDto>(Wire.Json), changed);
            }));
        }
        finally
        {
            await Save(original);
            await AwaitAppearance(colony, original);
        }
    }
    private Task<JsonElement> Save(ColonyAppearanceDto? appearance) => run.InvokeClientAsync("A", "SaveColonyAppearance", new
    { mode = appearance?.Mode, iconDefName = appearance?.IconDefName, colorDefName = appearance?.ColorDefName, colorHex = appearance?.ColorHex });
    private ColonyAppearanceDto? Read(string colony) => world.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-A" && s.ColonyId == colony).Appearance;
    private async Task AwaitAppearance(string colony, ColonyAppearanceDto? expected)
    {
        var watch = Stopwatch.StartNew();
        while (!Matches(Read(colony), expected) && watch.Elapsed.TotalSeconds < 90) await Task.Delay(100);
        Check(Matches(Read(colony), expected), "Colony appearance was not registered on server.");
    }
    internal static bool Matches(ColonyAppearanceDto? actual, ColonyAppearanceDto? expected) => actual?.Mode == expected?.Mode
        && actual?.IconDefName == expected?.IconDefName && actual?.ColorDefName == expected?.ColorDefName && actual?.ColorHex == expected?.ColorHex;
}
