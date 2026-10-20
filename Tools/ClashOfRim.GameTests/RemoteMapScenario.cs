using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class RemoteMapScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony)
{
    internal async Task ObserveAlly(bool verifyIsolation, Func<Task>? inspectMap = null, Func<Task>? beforeOpen = null)
    {
        await polling.Idle("B"); await run.InvokeClientAsync("B", "StartManualSnapshotUpload"); await polling.Idle("B");
        await run.InvokeClientAsync("A", "StartSyncWorldMapMarkers");
        var markers = await polling.Wait("A", "clash.records", new { kind = "markers" }, p => Items(p).Any(v => v.GetProperty("data").String("ownerUserId") == "rimtest-B"));
        var target = Items(markers).Single(v => v.GetProperty("data").String("ownerUserId") == "rimtest-B" && v.GetProperty("data").String("kind") == "TradeableColony");
        int before = (await run.CallAsync("A", "game.state")).Int("mapCount");
        string snapshot = (await run.CallAsync("A", "clash.state")).String("currentSnapshotId");
        var homeBefore = verifyIsolation ? await run.CallAsync("A", "clash.stability.map") : default;
        var targetBefore = verifyIsolation ? await run.CallAsync("B", "clash.stability.map") : default;
        if (beforeOpen is not null) await beforeOpen();
        await run.InvokeClientAsync("A", "StartObserveRemoteMarker", new { target = target.GetProperty("reference"), mode = "FriendlyObservation" });
        var remote = await polling.Wait("A", "clash.remote", null, s => s.Bool("active") && s.GetProperty("session").Bool("isObservation"));
        Check(remote.GetProperty("session").String("targetUserId") == "rimtest-B", "Wrong observation owner.");
        Check((await run.CallAsync("A", "game.state")).Int("mapCount") == before + 1, "Remote root map did not open alone.");
        if (inspectMap is not null) await inspectMap();
        if (verifyIsolation) await run.CallAsync("A", "clash.fixture.remoteLoss", new { acknowledgeFixture = true });
        Check((await run.CallAsync("A", "clash.remote.close")).Bool("invoked"), "Observation close was not accepted.");
        await polling.Wait("A", "clash.remote", null, s => !s.Bool("active"));
        Check((await run.CallAsync("A", "game.state")).Int("mapCount") == before && (await run.CallAsync("A", "clash.state")).String("currentSnapshotId") == snapshot,
            "Closing observation changed home map count or authoritative snapshot.");
        if (verifyIsolation)
        {
            Equal(homeBefore, await run.CallAsync("A", "clash.stability.map"), "Observation mutated viewer home inventory/buildings");
            Equal(targetBefore, await run.CallAsync("B", "clash.stability.map"), "Observation mutated target inventory/buildings");
        }
    }
    internal async Task ReloadBattle(string eventId)
    {
        await colony.PauseWhenIdle("A");
        var before = await run.CallAsync("A", "clash.remote");
        await colony.ReloadPaused("A", "rimtest-active-battle");
        var after = await polling.Wait("A", "clash.remote", null, s => s.Bool("active"));
        Check(after.GetProperty("session").String("relatedEventId") == eventId && after.GetProperty("session").Bool("isRaidBattle"), "Reload lost active battle identity.");
        foreach (string field in new[] { "targetSnapshotId", "targetMapId", "clientMapId" })
            Equal(before.GetProperty("session").GetProperty(field), after.GetProperty("session").GetProperty(field), "Reload changed battle " + field);
    }
}
