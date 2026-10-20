using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class RemoteMapPerformanceScenario(TestRun run, ColonyScenarioFixture colony, RemoteMapScenario remote, string root)
{
    internal async Task Run()
    {
        foreach (string client in new[] { "A", "B" }) await colony.PauseWhenIdle(client);
        await run.CallAsync("A", "clash.ui.configure", new { acknowledgeFixture = true, width = 1280, height = 720, scale = 1f });
        var samples = new List<object>();
        async Task Begin()
        {
            await run.CallAsync("A", "clash.remote.performance.start", new { acknowledgeFixture = true });
            // Establish callbacks before the action so the long event cannot hide
            // the stall at the boundary of the first observed frame.
            await Task.Delay(1000);
        }
        async Task Collect(string name, bool loading)
        {
            await Task.Delay(1500);
            var sample = await run.CallAsync("A", "clash.remote.performance.stop", new { acknowledgeFixture = true });
            var spans = sample.GetProperty("spans").EnumerateArray().ToArray();
            var open = spans.SingleOrDefault(s => s.String("name") == "ClientObservationRuntime.Open");
            var action = spans.SingleOrDefault(s => s.String("name") == "ClientObservationMaps.OpenAsync");
            var repaints = sample.GetProperty("repaints").GetProperty("timesMs").EnumerateArray().Select(t => t.GetDouble()).ToArray();
            object? load = open.ValueKind == JsonValueKind.Undefined || action.ValueKind == JsonValueKind.Undefined ? null : new
            {
                actionToFirstRepaintMs = repaints.First(t => t >= open.GetProperty("endMs").GetDouble()) - action.GetProperty("startMs").GetDouble(),
                repaintCountDuringOpen = repaints.Count(t => t >= open.GetProperty("startMs").GetDouble() && t <= open.GetProperty("endMs").GetDouble())
            };
            samples.Add(new { name, load, sample });
            await File.WriteAllTextAsync(Path.Combine(root, "remote-map-performance.json"), JsonSerializer.Serialize(samples, Wire.Json));
            Check(!sample.Bool("truncated"), "Timing sample exceeded its bounded capacity.");
            foreach (string callback in new[] { "updates", "repaints" })
                Check(sample.GetProperty(callback).Int("count") >= 10, "Real graphical callbacks were not observed: " + callback);
            if (loading)
            {
                foreach (string stage in new[] { "ClientObservationMaps.OpenAsync", "ClientRemoteSnapshotDownload.DownloadAsync",
                    "ClientObservationRuntime.Open", "RemoteMapSnapshotProjector.TryProjectPrepared", "RemoteMapRuntimeLoader.TryLoad",
                    "Map.FinalizeLoading", "MapDrawer.RegenerateEverythingNow" })
                    Check(spans.Any(s => s.String("name") == stage), "Missing timing stage: " + stage);
                var preparation = spans.Single(s => s.String("name") == "ClientRemoteSnapshotDocument.Read");
                Check(preparation.Int("startThread") != open.Int("startThread")
                    && preparation.GetProperty("endMs").GetDouble() <= open.GetProperty("startMs").GetDouble(),
                    "Snapshot parsing ran on the game thread, ran twice or overlapped map opening.");
                Check(spans.All(s => s.GetProperty("error").ValueKind == JsonValueKind.Null), "A measured loading stage failed.");
            }
            Console.WriteLine($"Remote performance {name}: repaint max gap={sample.GetProperty("repaints").GetProperty("maxGapMs").GetDouble():F1}ms");
        }
        await Begin(); await Collect("idle-before", false);
        for (int attempt = 1; attempt <= 3; attempt++)
            await remote.ObserveAlly(false, () => Collect("open-" + attempt, true), Begin);
        await Begin(); await Collect("idle-after", false);
    }
}
