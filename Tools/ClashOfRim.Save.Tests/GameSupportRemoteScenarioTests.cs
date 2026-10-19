using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameSupportRemoteScenarioTests
{
    internal static void Remote() => RemoteAsync().GetAwaiter().GetResult();
    internal static void Support() => SupportAsync().GetAwaiter().GetResult();
    private static async Task RemoteAsync()
    {
        foreach (string fault in new[] { "none", "basic", "owner", "no-map", "close-rejected", "map-leak", "snapshot", "home", "target" })
        {
            using var f = await GameScenarioTestRuntime.Create(); bool active = false, closed = false;
            f.Script = (client, method, args) => method switch
            {
                "clash.records" => List(new { reference = new { handle = "target-marker" }, data = new { ownerUserId = "rimtest-B", kind = "TradeableColony" } }),
                "clash.invoke" when args.String("action") == "StartObserveRemoteMarker" => Open(args),
                "clash.remote" => new { active, session = new { isObservation = true, targetUserId = fault == "owner" ? "wrong" : "rimtest-B" } },
                "game.state" => new { mapCount = active ? (fault == "no-map" ? 1 : 2) : closed && fault == "map-leak" ? 2 : 1 },
                "clash.state" => new { currentSnapshotId = closed && fault == "snapshot" ? "changed" : "original" },
                "clash.stability.map" => new { items = closed && (fault == "home" && client == "A" || fault == "target" && client == "B") ? 0 : 5 },
                "clash.remote.close" => Close(),
                _ => null
            };
            object Open(JsonElement args)
            {
                var input = args.GetProperty("arguments");
                Check(input.String("mode") == "FriendlyObservation" && input.GetProperty("target").String("handle") == "target-marker", "Observation opened the wrong target or mode.");
                active = true; return new { };
            }
            object Close() { active = false; closed = true; return new { invoked = fault != "close-rejected" }; }
            var scenario = new RemoteMapScenario(f.Run, f.Polling, new(f.Run, f.Polling));
            if (fault is not ("none" or "basic")) await Reject<AssertionException>(() => scenario.ObserveAlly(true), fault);
            else
            {
                await scenario.ObserveAlly(fault != "basic");
                Check(f.Count("clash.fixture.remoteLoss") == (fault == "basic" ? 0 : 1) && f.Count("clash.stability.map") == (fault == "basic" ? 0 : 4),
                    "Remote isolation assertions were skipped or applied to the basic profile.");
                var actions = f.Calls.Where(c => c.Method == "clash.invoke").Select(c => (c.Client, c.Args.String("action")));
                Check(actions.SequenceEqual(new[] { ("B", "StartManualSnapshotUpload"), ("A", "StartSyncWorldMapMarkers"), ("A", "StartObserveRemoteMarker") }),
                    "Remote observation lost target upload or marker refresh ordering.");
            }
        }
        foreach (string fault in new[] { "none", "event", "mode", "targetSnapshotId", "targetMapId", "clientMapId" })
        {
            using var f = await GameScenarioTestRuntime.Create(); bool loaded = false;
            f.Script = (_, method, _) => method switch
            {
                "game.state" => new { epoch = loaded ? "new" : "old", playerHasControl = true, mapCount = 2 },
                "game.load" => Load(),
                "clash.remote" => new { active = true, session = new { relatedEventId = loaded && fault == "event" ? "other" : "battle", isRaidBattle = !(loaded && fault == "mode"),
                    targetSnapshotId = loaded && fault == "targetSnapshotId" ? "different" : "snapshot", targetMapId = loaded && fault == "targetMapId" ? "different" : "target-map", clientMapId = loaded && fault == "clientMapId" ? "different" : "client-map" } },
                _ => null
            };
            object Load() { loaded = true; return new { }; }
            var scenario = new RemoteMapScenario(f.Run, f.Polling, new(f.Run, f.Polling));
            if (fault != "none") await Reject<AssertionException>(() => scenario.ReloadBattle("battle"), fault);
            else await scenario.ReloadBattle("battle");
            Check(f.Count("game.save") == 1 && f.Count("game.load") == 1 && f.Calls.Single(c => c.Method == "game.save").Args.String("name") == f.Calls.Single(c => c.Method == "game.load").Args.String("name"),
                "Battle reloaded another save or duplicated the mutation.");
        }
    }
    private static async Task SupportAsync()
    {
        await Transfer("early", "none", verifyState: false);
        foreach (string mode in new[] { "early", "reject", "expiry", "permanent", "death" }) await Transfer(mode, "none");
        foreach (string fault in new[] { "duration", "delivery-state", "return-state", "duplicate-assignment", "return-rejected", "quest-reason", "resident-copy", "name", "race", "kind", "gender", "biologicalAge", "ideologyRegistered", "culture", "foundation", "memes", "precepts" })
            await Transfer("early", fault);
        await Transfer("reject", "rejected-assignment");
        await Transfer("permanent", "permanent-assignment");
        await Transfer("permanent", "permanent-replay");
        await Transfer("death", "resurrection");
        await Transfer("death", "unfinished-quest");
    }
    private static async Task Transfer(string mode, string fault, bool verifyState = true)
    {
        using var f = await GameScenarioTestRuntime.Create(); var ledger = new InMemoryAuthoritativeEventLedger();
        var events = new EventScenarioFixture(f.Polling, ledger); var caravan = new CaravanScenario(f.Run, f.Polling, f.Dialogs, new(f.Run, f.Polling));
        caravan.Use(Wire.Element(new { reference = new { handle = "outbound" } }));
        var scenario = new SupportScenario(f.Run, f.Polling, f.Dialogs, caravan, ledger, events);
        int stage = 0, acceptances = 0; const string eventId = "support-event";
        f.Script = (client, method, args) => method switch
        {
            "clash.events" => List(new { data = new { eventId = "old-event", payloadSummary = "SupportDurationDays" } },
                new { data = new { eventId = stage > 0 ? eventId : "unrelated", payloadSummary = stage > 0 ? "SupportDurationDays" : "unrelated" } }),
            "caravans.list" => stage == 4 && fault != "resurrection" ? List() : List(new { reference = new { handle = stage >= 3 ? "returned" : "outbound" }, pawns = new[] { new { handle = "pawn-A" } } }),
            "clash.inventory" => Inventory(),
            "clash.pawn.identity" => Identity(client),
            "clash.stability.pawn" => new { data = new { skill = (client == "B" && fault == "delivery-state" || client == "A" && stage == 3 && fault == "return-state") ? 0 : 12 } },
            "clash.labels" => new { support = "支援" },
            "ui.windows" => List(new { reference = new { handle = "duration", type = "Mod.SupportDurationDialogWindow" } }),
            "clash.support.confirmDuration" => Send(args),
            "clash.invoke" when args.String("action") == "HandleEventLetterAction" => Decide(args.GetProperty("arguments")),
            "clash.support.assignments" => Assignments(),
            "clash.support.return" => Return(),
            "clash.fixture.supportDue" => Due(),
            "clash.stability.support" => List(new { eventId, completed = fault != "unfinished-quest", finishRequested = true, finishReason = fault == "quest-reason" ? "Wrong" : mode == "expiry" ? "QuestExpired" : "EarlyReturn" }),
            _ => GameGiftTestUi.Reply(method, "支援", 0)
        };
        object Send(JsonElement args)
        {
            Check(args.Int("days") == 3 && args.Bool("permanent") == (mode == "permanent"), "Support duration/permanent flag changed.");
            stage = 1;
            ledger.Append(new(eventId, ServerEventType.SupportPawn, new("rimtest-A"), new("rimtest-B"), DateTimeOffset.UnixEpoch,
                ServerEventStatus.ReadyForImmediateDelivery, ServerEventDeliveryMode.OnlineImmediate, "support-key", new SupportPawnEventPayload("pawn", "source", "pawn", true, null)));
            return new { };
        }
        object Decide(JsonElement args)
        {
            Check(args.String("eventId") == eventId, "Support selected an old/unrelated event.");
            if (args.String("actionKind") == "Reject") stage = 3;
            else { acceptances++; if (stage == 1) stage = 2; }
            return new { returnValue = true };
        }
        object Return() { stage = 3; return new { accepted = fault != "return-rejected" }; }
        object Due()
        {
            stage = mode == "death" ? 4 : 3;
            if (stage == 4)
            {
                ledger.ChangeStatus(eventId, ServerEventStatus.Failed);
                var loss = AuthoritativeEventFactory.Create(ServerEventType.SupportPawn, new("rimtest-B"), new("rimtest-A"), "loss", true,
                    new SupportPawnEventPayload("pawn", "source", "pawn", false, null, ReturnToSender: true, SourceEventId: eventId), DateTimeOffset.UnixEpoch);
                ledger.Append(loss); ledger.MarkDelivered(loss.EventId, "delivery", DateTimeOffset.UnixEpoch); ledger.MarkApplied(loss.EventId, "applied", DateTimeOffset.UnixEpoch);
            }
            return new { };
        }
        object Assignments()
        {
            bool assigned = stage == 2 && mode != "permanent" || fault == "rejected-assignment" && stage == 3 || fault == "permanent-assignment" && stage == 2;
            if (!assigned) return List();
            return List(Enumerable.Range(0, fault == "duplicate-assignment" && acceptances > 1 ? 2 : 1).Select(_ => (object)new { eventId, supportDurationDays = fault == "duration" ? 4 : 3, pawnThingId = "transferred" }).ToArray());
        }
        object Inventory()
        {
            int count = stage == 2 || stage == 3 && fault == "resident-copy" || stage == 4 && fault == "resurrection" ? 3 : 2;
            if (mode == "permanent" && fault == "permanent-replay" && acceptances > 1) count = 4;
            return new { maps = new[] { new { colonists = Enumerable.Range(0, count).Select(i => new { id = "resident-" + i, reference = new { handle = "resident-" + i } }).ToArray() } } };
        }
        object Identity(string client)
        {
            var identity = new Dictionary<string, object?> { ["name"] = "pawn", ["race"] = "Human", ["kind"] = "Colonist", ["gender"] = "Female", ["biologicalAge"] = 20,
                ["ideologyRegistered"] = !(client == "B" && fault == "ideologyRegistered") };
            var ideology = new Dictionary<string, object?> { ["culture"] = "culture", ["foundation"] = "foundation", ["memes"] = new[] { "meme" }, ["precepts"] = new[] { "precept" } };
            if (client == "B" && identity.ContainsKey(fault) && fault != "ideologyRegistered") identity[fault] = "changed";
            if (client == "B" && ideology.ContainsKey(fault)) ideology[fault] = "changed";
            identity["ideology"] = ideology; return identity;
        }
        if (fault != "none") { await Reject<AssertionException>(() => scenario.Run(mode, true), mode + "/" + fault); return; }
        if (verifyState) await scenario.Run(mode, true); else await scenario.Run();
        Check(f.Count("clash.support.confirmDuration") == 1 && acceptances == (mode == "reject" ? 0 : mode == "death" ? 3 : verifyState ? 2 : 1), "Support skipped or repeated its send/replay checks.");
        if (!verifyState) Check(f.Count("clash.stability.pawn") == 0 && f.Count("clash.stability.support") == 0, "Basic support unexpectedly required stability fixtures.");
        Check(f.Count("time.run") == (mode is "permanent" or "reject" ? 0 : 1) && f.Count("time.cancel") == f.Count("time.run"), "Support return left ticking active.");
        if (mode is "early" or "reject" or "expiry") Check(caravan.Handle == "returned", "Support return did not publish the replacement caravan handle.");
        Check(f.Count("clash.fixture.supportDue") == (mode is "expiry" or "death" ? 1 : 0)
            && f.Count("clash.fixture.supportDeath") == (mode == "death" ? 1 : 0), "Support scenario drove another lifecycle branch.");
    }
    private static object List(params object[] items) => new { items };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action, string context) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + context);
    }
}
