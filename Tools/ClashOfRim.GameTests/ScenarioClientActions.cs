using System.Text.Json;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal static class ScenarioClientActions
{
    internal static Task<JsonElement> InvokeClientAsync(this TestRun run, string client, string action, object? input = null)
        => run.CallAsync(client, "clash.invoke", new { action, arguments = input ?? new { } });
}
