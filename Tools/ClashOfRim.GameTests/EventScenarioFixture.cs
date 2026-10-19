using System.Diagnostics;
using AIRsLight.ClashOfRim.Events;
using System.Text.Json;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed class EventScenarioFixture(ScenarioPolling polling, IAuthoritativeEventLedger ledger)
{
    internal async Task<AuthoritativeEvent> AwaitLedger(string id, Func<AuthoritativeEvent, bool> predicate, int seconds = 90)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            if (ledger.Find(id) is { } current && predicate(current)) return current;
            await Task.Delay(100);
        }
        throw new Exception($"Ledger event {id} did not reach expected state: {ledger.Find(id)}");
    }

    internal Task<JsonElement> Receive(string client, string id) => polling.Wait(client, "clash.events", new { limit = 200 },
        p => Items(p).Any(v => v.GetProperty("data").String("eventId") == id));

}
