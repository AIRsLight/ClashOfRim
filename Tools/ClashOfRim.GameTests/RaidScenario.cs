using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed record RaidScenarioResult(string EventId, int? DefenderLossCount);

internal sealed class RaidScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs,
    CaravanScenario caravanFixture, RemoteMapScenario remoteMaps, ServerEntryScenario entry,
    IColonySnapshotIndexStore snapshots, DiplomacyRelationRegistry relations,
    IAuthoritativeEventLedger ledger, EventScenarioFixture eventFixture)
{
    internal async Task<RaidScenarioResult> Run(bool verifyLoss)
    {
        await polling.Idle("A"); await polling.Idle("B");
        string attackerColony = (await run.CallAsync("A", "clash.state")).String("colonyId");
        string defenderColony = (await run.CallAsync("B", "clash.state")).String("colonyId");
        await run.InvokeClientAsync("A", "StartRefreshAdminStatus"); await polling.Idle("A");
        var original = (await run.CallAsync("A", "clash.admin")).GetProperty("configuration");
        var configuration = JsonNode.Parse(original.GetRawText())!;
        configuration["diplomacyRelationChangeCooldownHours"] = 0;
        configuration["raidMinimumDefenderWealth"] = 0;
        configuration["raidProtectionHours"] = 0;
        configuration["pvpEnabled"] = true;
        if (verifyLoss) configuration["raidSettlementLossRatio"] = 1;
        try
        {
            await run.InvokeClientAsync("A", "StartUpdateAdminConfiguration", new { configuration }); await polling.Idle("A");
            foreach (var (kind, expected) in new[] { ("AllianceCancellation", "Neutral"), ("WarDeclaration", "Hostile") })
            {
                await run.InvokeClientAsync("A", "StartRefreshPlayers"); await polling.Idle("A");
                var target = Items(await run.CallAsync("A", "clash.records", new { kind = "players" })).Single(v => v.GetProperty("data").String("userId") == "rimtest-B");
                await run.InvokeClientAsync("A", "StartCreateDiplomacyEvent", new { kind, target = target.GetProperty("reference"), customMessage = "RimTest raid preparation" });
                await polling.Idle("A");
                var watch = Stopwatch.StartNew();
                while (relations.GetRelationKind("rimtest-A", attackerColony, "rimtest-B", defenderColony) != expected && watch.Elapsed.TotalSeconds < 30)
                    await Task.Delay(100);
                Check(relations.GetRelationKind("rimtest-A", attackerColony, "rimtest-B", defenderColony) == expected, "Diplomacy transition failed: " + kind);
            }
            await run.InvokeClientAsync("B", "StartManualSnapshotUpload"); await polling.Idle("B");
            string defenderSnapshot = snapshots.GetLatest("rimtest-B", defenderColony)?.Identity.SnapshotId ?? throw new Exception("Missing persisted defender snapshot identity.");
            await entry.ExitSession("B");
            // Refresh the same world catalog a player sees until the logout reaches the server.
            JsonElement marker = default;
            for (int i = 0; i < 30; i++)
            {
                await run.InvokeClientAsync("A", "StartSyncWorldMapMarkers"); await Task.Delay(500);
                var candidates = Items(await run.CallAsync("A", "clash.records", new { kind = "markers" })).Where(m => m.GetProperty("data").String("ownerUserId") == "rimtest-B" && m.GetProperty("data").Bool("canRaid")).ToArray();
                if (candidates.Length == 1) { marker = candidates[0]; break; }
            }
            Check(marker.ValueKind == JsonValueKind.Object, "Offline hostile colony did not become raidable.");
            var caravan = Items(await run.CallAsync("A", "caravans.list")).Single(c => c.GetProperty("pawns").GetArrayLength() == 1);
            caravanFixture.Use(caravan);
            await run.CallAsync("A", "world.select", new { target = caravanFixture.Handle });
            string raidLabel = (await run.CallAsync("A", "clash.labels")).String("raid");
            var raid = Items(await run.CallAsync("A", "world.gizmos", new { target = caravanFixture.Handle })).Single(g => g.String("label") == raidLabel && !g.Bool("disabled"));
            await run.CallAsync("A", "world.invokeGizmo", new { target = caravanFixture.Handle, type = raid.String("type"), label = raid.String("label") });
            await dialogs.ChooseOnlyMenu("A"); await dialogs.Confirm("A");
            var remote = await polling.Wait("A", "clash.remote", null, r => r.Bool("active") && r.GetProperty("session").Bool("isRaidBattle"));
            string eventId = remote.GetProperty("session").String("relatedEventId");
            Check(remote.GetProperty("session").String("targetSnapshotId") == defenderSnapshot, "Raid loaded the wrong defender baseline.");
            await polling.Idle("A"); // The map opens before the departure snapshot transaction ends.
            JsonElement loss = default;
            if (verifyLoss)
            {
                loss = await run.CallAsync("A", "clash.fixture.remoteLoss", new { acknowledgeFixture = true });
                await remoteMaps.ReloadBattle(eventId);
            }
            // Explicitly test finishing with the attack force still present (the original warning is retained).
            Check((await run.CallAsync("A", "clash.remote.close")).Bool("invoked"), "Raid finish action rejected.");
            await dialogs.Confirm("A");
            await polling.Wait("A", "clash.remote", null, r => !r.Bool("active")); await polling.Idle("A");
            var settledEvent = await eventFixture.AwaitLedger(eventId, e => e.Status == ServerEventStatus.AppliedToSnapshot);
            await run.RecordAsync(new { serverObservation = "raid-settlement", eventId, status = settledEvent.Status.ToString() });
            string? settled = snapshots.GetLatest("rimtest-B", defenderColony)?.Identity.SnapshotId;
            Check(!string.IsNullOrWhiteSpace(settled) && settled != defenderSnapshot, "Defender settlement did not publish a snapshot.");
            Check((await run.CallAsync("A", "game.state")).Int("mapCount") == 1, "Battle map remained after settlement.");
            if (verifyLoss)
            {
                int events = ledger.ListAll().Count;
                Check(!(await run.CallAsync("A", "clash.remote.close")).Bool("invoked"), "Closed battle accepted a second settlement.");
                await polling.Idle("A");
                Check(ledger.ListAll().Count == events && snapshots.GetLatest("rimtest-B", defenderColony)?.Identity.SnapshotId == settled, "Repeated finish mutated settled state.");
            }
            return new(eventId, verifyLoss ? loss.GetProperty("before").Int("count") : null);
        }
        finally
        {
            await run.InvokeClientAsync("A", "StartUpdateAdminConfiguration", new { configuration = original }); await polling.Idle("A");
        }
    }
}
