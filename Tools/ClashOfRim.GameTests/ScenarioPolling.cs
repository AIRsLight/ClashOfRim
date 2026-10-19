using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

// Owns the suite polling policy, operation completion and ordinary-pause handling.
internal sealed class ScenarioPolling(TestRun run, ScenarioDialogs dialogs)
{
    internal WaitOptions Waiting(string client, string method, int seconds = 90) => new(TimeSpan.FromSeconds(seconds), AfterUnmet: async (value, token) =>
    {
        if (method == "clash.state") await dialogs.AcceptLanguageWarning(client);
        // The flight ceremony opens the original abandon-map warning after ticking.
        // Resolve it explicitly and retain the dialog/button evidence in the trace.
        if (method == "clash.gravship") await dialogs.Confirm(client);
        // This suite explicitly resumes ordinary event pauses, while forced modal pauses remain effective.
        // RimTest itself never makes this gameplay-policy decision.
        var ticking = method == "time.status" ? value : await run.CallAsync(client, "time.status", token: token);
        if (ticking.OptionalString("state") == "running" && ticking.Bool("paused") && !ticking.Bool("forcedPause"))
            await run.CallAsync(client, "time.resume", token: token);
    });
    internal Task<JsonElement> Wait(string client, string method, object? input, Func<JsonElement, bool> predicate, int seconds = 90) =>
        run.EventuallyAsync(client, method, input, predicate, Waiting(client, method, seconds));
    internal async Task<JsonElement> RunUntil(string client, string method, object? input, Func<JsonElement, bool> predicate, int seconds = 90)
    {
        try
        {
            await run.CallAsync(client, "time.run", new { tps = 1200 });
            return await Wait(client, method, input, predicate, seconds);
        }
        finally { await run.CallAsync(client, "time.cancel"); }
    }
    internal Task<JsonElement> Idle(string client) => Wait(client, "clash.state", null, s =>
        !s.Bool("manualSyncInProgress") && !s.Bool("snapshotUploadInProgress") && !s.Bool("localAtomicMutationPending") && !s.Bool("bankInProgress") && !s.Bool("serverShopInProgress") && !s.Bool("mercenaryInProgress") && !s.Bool("adminInProgress") && !s.Bool("tradeOrdersPageLoadInProgress"));
    internal async Task Step(string client, int ticks)
    {
        var operation = await run.RunOperationAsync(client, "time.step", new { ticks, tps = 1200 }, "time.status", null,
            (accepted, current) => current.OptionalString("id") == accepted.String("id") && current.String("state") == "completed",
            Waiting(client, "time.status"), failed: (accepted, current) => current.OptionalString("id") == accepted.String("id") && current.String("state") is "cancelled" or "failed" ? "Tick operation stopped: " + current.GetRawText() : null);
        RimTest.Testing.Check.Equal(ticks, operation.LastObservation.Int("advancedTicks"), "Tick budget did not complete");
    }
}
