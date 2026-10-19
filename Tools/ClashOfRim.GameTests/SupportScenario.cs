using AIRsLight.ClashOfRim.Events;
using System.Text.Json;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class SupportScenario(TestRun run, ScenarioPolling polling, ScenarioDialogs dialogs,
    CaravanScenario caravanFixture, IAuthoritativeEventLedger ledger, EventScenarioFixture events)
{
    internal Task Run() => Run("early", false);
    internal async Task Run(string mode, bool verifyState)
    {
        await polling.Idle("A"); await polling.Idle("B");
        var previousEvents = Items(await run.CallAsync("B", "clash.events", new { limit = 200 })).Select(v => v.GetProperty("data").String("eventId")).ToHashSet();
        var originalCaravan = Items(await run.CallAsync("A", "caravans.list")).Single(c => H(c) == caravanFixture.Handle);
        var originalPawn = originalCaravan.GetProperty("pawns")[0];
        var identity = await run.CallAsync("A", "clash.pawn.identity", new { pawn = originalPawn.String("handle") });
        var originalState = verifyState ? await run.CallAsync("A", "clash.stability.pawn", new { pawn = originalPawn.String("handle") }) : default;
        var targetPawns = (await run.CallAsync("B", "clash.inventory")).GetProperty("maps")[0].GetProperty("colonists").EnumerateArray().Select(p => p.String("id")).ToHashSet();
        await run.CallAsync("A", "world.select", new { target = caravanFixture.Handle });
        string supportLabel = (await run.CallAsync("A", "clash.labels")).String("support");
        var gizmo = Items(await run.CallAsync("A", "world.gizmos", new { target = caravanFixture.Handle })).Single(g => g.String("label") == supportLabel && !g.Bool("disabled"));
        await run.CallAsync("A", "world.invokeGizmo", new { target = caravanFixture.Handle, type = gizmo.String("type"), label = gizmo.String("label") });
        await dialogs.ChooseOnlyMenu("A"); await dialogs.Confirm("A");
        string window = H(Items(await run.CallAsync("A", "ui.windows")).Single(w => w.GetProperty("reference").String("type").EndsWith(".SupportDurationDialogWindow")));
        await run.CallAsync("A", "clash.support.confirmDuration", new { window, days = 3, permanent = mode == "permanent" }); await polling.Idle("A");
        bool Incoming(JsonElement v) => !previousEvents.Contains(v.GetProperty("data").String("eventId")) && v.GetProperty("data").String("payloadSummary").Contains("SupportDurationDays", StringComparison.OrdinalIgnoreCase);
        var incoming = await polling.Wait("B", "clash.events", new { limit = 200 }, p => Items(p).Any(Incoming));
        var request = Items(incoming).Single(Incoming);
        string id = request.GetProperty("data").String("eventId");
        if (mode == "reject")
        {
            Check((await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Reject" })).Bool("returnValue"), "Support rejection rejected.");
            await polling.Idle("B"); await AwaitSupportReturn(originalState, verifyState);
            Check(!Items(await run.CallAsync("B", "clash.support.assignments")).Any(s => s.String("eventId") == id), "Rejected support became an assignment.");
            return;
        }
        Check((await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" })).Bool("returnValue"), "Support acceptance rejected.");
        if (mode == "permanent")
        {
            var landed = await polling.Wait("B", "clash.inventory", null, p => p.GetProperty("maps")[0].GetProperty("colonists").GetArrayLength() == targetPawns.Count + 1);
            var pawn = landed.GetProperty("maps")[0].GetProperty("colonists").EnumerateArray().Single(p => !targetPawns.Contains(p.String("id")));
            Equal(originalState.GetProperty("data"), (await run.CallAsync("B", "clash.stability.pawn", new { pawn = H(pawn) })).GetProperty("data"), "Permanent support changed stable state");
            Check(!Items(await run.CallAsync("B", "clash.support.assignments")).Any(s => s.String("eventId") == id), "Permanent support incorrectly acquired an expiry assignment.");
            await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" }); await polling.Idle("B"); await polling.Step("B", 600);
            Check((await run.CallAsync("B", "clash.inventory")).GetProperty("maps")[0].GetProperty("colonists").GetArrayLength() == targetPawns.Count + 1, "Repeated permanent acceptance duplicated pawn.");
            return;
        }
        var assigned = await polling.Wait("B", "clash.support.assignments", null, p => Items(p).Any(s => s.String("eventId") == id));
        Check(Items(assigned).Single(s => s.String("eventId") == id).Int("supportDurationDays") == 3, "Support duration did not persist.");
        await polling.Idle("B"); await polling.Step("B", 600);
        if (verifyState)
        {
            var receivedState = await run.CallAsync("B", "clash.stability.pawn", new { thingId = Items(assigned).Single(s => s.String("eventId") == id).String("pawnThingId") });
            Equal(originalState.GetProperty("data"), receivedState.GetProperty("data"), "Support delivery changed stable pawn state");
            int count = Items(assigned).Length;
            await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" }); await polling.Idle("B");
            Check(Items(await run.CallAsync("B", "clash.support.assignments")).Length == count, "Repeated support acceptance duplicated assignments.");
        }
        var receivedIdentity = await run.CallAsync("B", "clash.pawn.identity", new { thingId = Items(assigned).Single(s => s.String("eventId") == id).String("pawnThingId") });
        foreach (string field in new[] { "name", "race", "kind", "gender", "biologicalAge" }) Equal(identity.GetProperty(field), receivedIdentity.GetProperty(field), "Support changed pawn " + field);
        Check(receivedIdentity.Bool("ideologyRegistered"), "Transferred pawn has a dangling ideology reference.");
        if (identity.GetProperty("ideology").ValueKind != JsonValueKind.Null)
        {
            var receivedIdeo = receivedIdentity.GetProperty("ideology");
            Check(receivedIdeo.ValueKind != JsonValueKind.Null, "Support lost pawn ideology.");
            foreach (string field in new[] { "culture", "foundation", "memes", "precepts" }) Equal(identity.GetProperty("ideology").GetProperty(field), receivedIdeo.GetProperty(field), "Support changed pawn ideology " + field);
        }
        if (mode == "death") await run.CallAsync("B", "clash.fixture.supportDeath", new { thingId = Items(assigned).Single(s => s.String("eventId") == id).String("pawnThingId"), acknowledgeFixture = true });
        if (mode is "expiry" or "death") await run.CallAsync("B", "clash.fixture.supportDue", new { eventId = id, acknowledgeFixture = true });
        else Check((await run.CallAsync("B", "clash.support.return", new { eventId = id })).Bool("accepted"), "Early support return rejected.");
        await polling.Idle("B");
        await run.CallAsync("B", "time.run", new { tps = 1200 });
        try { await polling.Wait("B", "clash.support.assignments", null, p => !Items(p).Any(s => s.String("eventId") == id)); }
        finally { await run.CallAsync("B", "time.cancel"); }
        if (mode == "death")
        {
            await polling.Idle("B"); await polling.Step("A", 600); await polling.Step("B", 600);
            await events.AwaitLedger(id, e => e.Status == ServerEventStatus.Failed, 30);
            var losses = ledger.ListAll().Where(e => e.Payload is AIRsLight.ClashOfRim.Events.SupportPawnEventPayload p && p.SourceEventId == id && p.ReturnToSender).ToArray();
            Check(losses.Length == 1 && ((AIRsLight.ClashOfRim.Events.SupportPawnEventPayload)losses[0].Payload).PawnPackage is null, "Death closure duplicated or returned a living pawn.");
            await events.AwaitLedger(losses[0].EventId, e => e.Status == ServerEventStatus.AppliedToSnapshot, 30);
            await run.InvokeClientAsync("B", "HandleEventLetterAction", new { eventId = id, actionKind = "Accept" }); await polling.Idle("B"); await polling.Step("B", 600);
            Check((await run.CallAsync("B", "clash.inventory")).GetProperty("maps")[0].GetProperty("colonists").GetArrayLength() == targetPawns.Count
                && !Items(await run.CallAsync("B", "clash.support.assignments")).Any(s => s.String("eventId") == id), "Repeated acceptance resurrected finished dead support.");
            Check(Items(await run.CallAsync("A", "caravans.list")).Sum(c => c.GetProperty("pawns").GetArrayLength()) == 0, "Dead support resurrected in owner's caravan.");
            var closed = Items(await run.CallAsync("B", "clash.stability.support", new { limit = 200 })).Single(q => q.String("eventId") == id);
            Check(closed.Bool("completed"), "Dead support quest remained active.");
            await run.RecordAsync(new { serverObservation = "support-death-closure", source = id, loss = losses[0].EventId });
            return;
        }
        await AwaitSupportReturn(originalState, verifyState);
        if (verifyState)
        {
            await polling.Step("B", 60);
            var quest = Items(await run.CallAsync("B", "clash.stability.support", new { limit = 200 })).Single(p => p.String("eventId") == id);
            Check(quest.Bool("completed") && quest.Bool("finishRequested") && quest.String("finishReason") == (mode == "expiry" ? "QuestExpired" : "EarlyReturn"), "Support quest did not reach its expected terminal state.");
            Check((await run.CallAsync("B", "clash.inventory")).GetProperty("maps")[0].GetProperty("colonists").GetArrayLength() == targetPawns.Count, "Support departure left a duplicate resident pawn.");
        }
    }
    async Task AwaitSupportReturn(JsonElement original, bool verify)
    {
        var caravans = await polling.Wait("A", "caravans.list", null, p => Items(p).Sum(c => c.GetProperty("pawns").GetArrayLength()) == 1);
        var caravan = Items(caravans).Single(c => c.GetProperty("pawns").GetArrayLength() == 1);
        caravanFixture.Use(caravan); await polling.Idle("A");
        if (verify)
        {
            var returned = await run.CallAsync("A", "clash.stability.pawn", new { pawn = caravan.GetProperty("pawns")[0].String("handle") });
            Equal(original.GetProperty("data"), returned.GetProperty("data"), "Support return changed stable pawn state");
        }
    }
}
