using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ScenarioRuntimeDiagnostics(TestRun run)
{
    private readonly Dictionary<string, HashSet<int>> startupErrors = new();

    internal async Task CaptureStartup(string client)
    {
        var hello = await run.CallAsync(client, "runtime.hello");
        startupErrors.TryAdd(client, Items(await run.CallAsync(client, "runtime.logs", new { limit = 200 }))
            .Where(e => hello.Bool("headless") && e.String("level") == "error"
                && e.String("message") == "Resolution too small (0x0), resetting UI scale to 1.")
            .Select(e => e.Int("sequence")).ToHashSet());
    }

    internal void CheckEntry(string client, JsonElement entry)
        => Check(entry.String("level") != "error" || startupErrors[client].Contains(entry.Int("sequence")),
            "Game error during entry: " + entry.String("message"));

    internal async Task Verify(IEnumerable<string> clients)
    {
        await run.CaptureLogsAsync();
        foreach (string client in clients)
        {
            var errors = run.Logs.Where(e => e.Client == client).Select(e => e.Entry).Where(e =>
                (e.String("level") is "error" or "bridge" or "probe" && !startupErrors[client].Contains(e.Int("sequence"))) ||
                e.String("message").Contains("[ClashOfRim] Main thread action failed:") ||
                e.String("message").Contains("[ClashOfRim][AutoEventApply] prepare failed")).ToArray();
            Check(errors.Length == 0, "Unexpected game errors: " + JsonSerializer.Serialize(errors));
        }
    }
}
