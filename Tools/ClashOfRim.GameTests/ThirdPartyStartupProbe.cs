using System.Text.Json;
using RimTest.Client;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

// Startup admission is deliberately separate from gameplay-effect coverage.
internal static class ThirdPartyStartupProbe
{
    internal static async Task<int> Run(string pipe, string root, string profilePath)
    {
        var spec = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath)).RootElement;
        var run = new TestRun(root, ["loaded-mods", "required-runtime", "runtime-errors"]);
        RimTestClient? client = null; bool passed = false;
        try
        {
            client = await RimTestClient.ConnectAsync(pipe, TimeSpan.FromSeconds(90));
            await run.AddClientAsync("game", client);
            var diagnostics = new ScenarioRuntimeDiagnostics(run);
            await diagnostics.CaptureStartup("game");
            await run.RunCaseAsync("loaded-mods", async () =>
            {
                await run.EventuallyAsync("game", "game.state", null, s => s.Bool("startupReady") && !s.Bool("playing"), new(TimeSpan.FromSeconds(120)));
                var hello = await run.CallAsync("game", "runtime.hello");
                var actual = hello.GetProperty("mods").EnumerateArray().Select(m => m.String("id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var expected = spec.GetProperty("mods").EnumerateArray().Select(m => m.GetString()!).Concat(
                    ["brrainz.harmony", "ludeon.rimworld", "airslight.rimtest", "airslight.clashofrim", "airslight.clashofrim.thirdpartycompat", "airslight.clashofrim.rimtestbridge"]);
                Check(actual.SetEquals(expected), "Loaded packages differ from the requested isolated profile.");
            });
            await run.RunCaseAsync("required-runtime", async () =>
            {
                var runtime = await run.CallAsync("game", "clash.thirdParty.runtime");
                var loaded = runtime.GetProperty("types").EnumerateArray().Where(t => t.Bool("loaded")).Select(t => t.String("name")).ToHashSet();
                foreach (var type in spec.GetProperty("types").EnumerateArray()) Check(loaded.Contains(type.GetString()!), "Missing third-party runtime: " + type.GetString());
            });
            await run.RunCaseAsync("runtime-errors", () => diagnostics.Verify(["game"]));
            passed = true;
        }
        catch (Exception ex)
        {
            await run.RecordAsync(new { failure = ex.ToString() });
            Console.Error.WriteLine(ex.Message);
            if (client is not null) await run.CollectDiagnosticsAsync(["clash.thirdParty.runtime", "runtime.logs", "ui.windows"]);
        }
        finally
        {
            passed = await run.SaveReportAsync(passed, new { scope = "third-party-startup-only", gameplayEffectsTested = false });
            if (client is not null) await client.DisposeAsync();
        }
        return passed ? 0 : 1;
    }
}
