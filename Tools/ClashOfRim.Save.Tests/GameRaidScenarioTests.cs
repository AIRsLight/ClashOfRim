using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameRaidScenarioTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> package) => RunAsync(package("raid-scenario-unit")).GetAwaiter().GetResult();
    private static async Task RunAsync(SaveSnapshotPackage package)
    {
        await Case(package, false, "none"); await Case(package, true, "none");
        foreach (string phase in new[] { "configure", "diplomacy", "marker", "open", "loss", "load", "settlement" }) await Case(package, true, phase);
        foreach (string fault in new[] { "wrong-baseline", "close-rejected", "snapshot-unchanged", "empty-snapshot", "blank-snapshot", "missing-snapshot", "map-leak", "replay-accepted", "replay-event", "replay-snapshot" }) await Case(package, true, fault);
    }
    private static async Task Case(SaveSnapshotPackage package, bool verifyLoss, string fault)
    {
        using var f = await GameScenarioTestRuntime.Create();
        var snapshots = new InMemoryColonySnapshotIndexStore(); var relations = new DiplomacyRelationRegistry(); var ledger = new InMemoryAuthoritativeEventLedger();
        const string attacker = "rebuilt-attacker", defender = "rebuilt-defender", eventId = "raid-event";
        var original = Wire.Element(new { giftsEnabled = true, pvpEnabled = false, diplomacyRelationChangeCooldownHours = 3.5,
            raidMinimumDefenderWealth = 4000, raidProtectionHours = 7, raidSettlementLossRatio = 0.2, unrelated = new { name = "保留 é İ" } });
        JsonElement current = original;
        bool active = false, defenderOnline = true, loaded = false, settled = false; int updates = 0, closes = 0, uploads = 0;
        var diplomacyActions = new List<string>();
        Store("baseline");
        relations.SetRelationKind("rimtest-A", attacker, "rimtest-B", defender, "Ally", null, DateTimeOffset.UnixEpoch);
        var colony = new ColonyScenarioFixture(f.Run, f.Polling); var caravan = new CaravanScenario(f.Run, f.Polling, f.Dialogs, colony);
        var scenario = new RaidScenario(f.Run, f.Polling, f.Dialogs, caravan, new(f.Run, f.Polling, colony), f.CreateEntry(), snapshots, relations, ledger, new(f.Polling, ledger));
        f.Script = (client, method, args) => method switch
        {
            "clash.state" => new { colonyId = client == "A" ? attacker : defender, isInActiveMultiplayerSession = client == "A" || defenderOnline },
            "clash.admin" => new { configuration = current },
            "clash.invoke" => Invoke(client, args),
            "game.returnToMenu" => Exit(),
            "game.state" => client == "B" ? new { epoch = "B", startupReady = !defenderOnline, hasGame = defenderOnline, playing = defenderOnline, playerHasControl = defenderOnline, mapCount = defenderOnline ? 1 : 0 }
                : new { epoch = loaded ? "new-A" : "old-A", startupReady = false, hasGame = true, playing = true, playerHasControl = true, mapCount = active || settled && fault == "map-leak" ? 2 : 1 },
            "clash.records" => args.String("kind") == "players" ? List(new { reference = new { handle = "player-B" }, data = new { userId = "rimtest-B" } })
                : List(new { data = new { ownerUserId = "rimtest-B", canRaid = true } }),
            "caravans.list" => List(new { reference = new { handle = "attack-caravan" }, pawns = new[] { new { handle = "attacker-pawn" } } }),
            "clash.labels" => new { raid = "袭击" },
            "world.gizmos" => List(new { label = "袭击", type = "raid-command", disabled = false }),
            "world.invokeGizmo" => Open(args),
            "clash.remote" => new { active, session = new { isRaidBattle = true, relatedEventId = eventId, targetSnapshotId = fault == "wrong-baseline" ? "other" : "baseline", targetMapId = "defender-map", clientMapId = "battle-map" } },
            "clash.fixture.remoteLoss" => Loss(args),
            "game.load" => Load(),
            "clash.remote.close" => Close(),
            _ => GameGiftTestUi.Reply(method, "袭击", 0)
        };
        object Invoke(string client, JsonElement args)
        {
            string action = args.String("action"); var input = args.GetProperty("arguments");
            switch (action)
            {
                case "StartUpdateAdminConfiguration":
                    Check(client == "A", "Raid configuration used the guest."); current = input.GetProperty("configuration").Clone();
                    if (++updates == 1)
                    {
                        Check(current.Bool("pvpEnabled") && current.Int("raidMinimumDefenderWealth") == 0 && current.Int("raidProtectionHours") == 0
                            && current.Int("diplomacyRelationChangeCooldownHours") == 0 && current.GetProperty("raidSettlementLossRatio").GetDouble() == (verifyLoss ? 1 : 0.2),
                            "Raid temporary configuration lost its scope.");
                        Fail("configure");
                    }
                    break;
                case "StartCreateDiplomacyEvent":
                    Check(input.GetProperty("target").String("handle") == "player-B", "Raid targeted another player.");
                    Fail("diplomacy"); string kind = input.String("kind"); diplomacyActions.Add(kind);
                    relations.SetRelationKind("rimtest-A", attacker, "rimtest-B", defender, kind == "AllianceCancellation" ? "Neutral" : "Hostile", null, DateTimeOffset.UnixEpoch);
                    break;
                case "StartManualSnapshotUpload": Check(client == "B", "Raid uploaded the attacker as defender."); uploads++; break;
                case "StartSyncWorldMapMarkers": Check(!defenderOnline, "Raid queried attack availability before defender exit."); Fail("marker"); break;
                case "StartRefreshAdminStatus" or "StartRefreshPlayers": break;
                default: throw new InvalidOperationException("Unexpected action: " + action);
            }
            return new { returnValue = true };
        }
        object Exit() { defenderOnline = false; return new { }; }
        object Open(JsonElement args)
        {
            Check(args.String("target") == "attack-caravan" && args.String("label") == "袭击" && args.String("type") == "raid-command", "Raid bypassed the selected caravan/gizmo.");
            Fail("open"); active = true;
            ledger.Append(new(eventId, ServerEventType.Raid, new("rimtest-A", attacker), new("rimtest-B", defender), DateTimeOffset.UnixEpoch,
                ServerEventStatus.ReadyForImmediateDelivery, ServerEventDeliveryMode.OnlineImmediate, "raid", new RaidEventPayload("baseline", null, DateTimeOffset.UnixEpoch, null, null)));
            return new { };
        }
        object Loss(JsonElement args) { Check(args.Bool("acknowledgeFixture"), "Battle loss fixture was not acknowledged."); Fail("loss"); return new { before = new { count = 7 } }; }
        object Load() { Fail("load"); loaded = true; return new { }; }
        object Close()
        {
            Fail("settlement"); closes++;
            if (closes == 1)
            {
                if (fault == "close-rejected") return new { invoked = false };
                active = false; settled = true;
                ledger.MarkDelivered(eventId, "battle", DateTimeOffset.UnixEpoch); ledger.MarkApplied(eventId, "settled", DateTimeOffset.UnixEpoch);
                if (fault != "snapshot-unchanged") Store(fault == "empty-snapshot" ? null : fault == "blank-snapshot" ? " " : "settled");
                if (fault == "missing-snapshot") snapshots.RemoveLatest("rimtest-B", defender);
                return new { invoked = true };
            }
            if (fault == "replay-event") ledger.Append(ledger.Find(eventId)! with { EventId = "duplicate-event", IdempotencyKey = "duplicate" });
            if (fault == "replay-snapshot") Store("changed-again");
            return new { invoked = fault == "replay-accepted" };
        }
        void Store(string? id)
        {
            var identity = new SnapshotIdentity("rimtest-B", defender, id);
            snapshots.StoreLatest(new(identity, package.Envelope with { Identity = identity }, package.Index, DateTimeOffset.UnixEpoch));
        }
        void Fail(string phase) { if (fault == phase) throw new IOException("scripted raid failure: " + phase); }
        if (fault == "none")
        {
            var result = await scenario.Run(verifyLoss);
            Check(result.EventId == eventId && result.DefenderLossCount == (verifyLoss ? 7 : null), "Raid result did not belong to this run or claimed unobserved loss.");
            Check(diplomacyActions.SequenceEqual(new[] { "AllianceCancellation", "WarDeclaration" }) && uploads == 1, "Raid skipped diplomacy or changed defender upload count.");
            Check(f.Count("clash.fixture.remoteLoss") == (verifyLoss ? 1 : 0) && f.Count("game.load") == (verifyLoss ? 1 : 0) && closes == (verifyLoss ? 2 : 1), "Raid stability branch omitted/repeated loss, reload or settlement replay.");
        }
        else if (fault is "configure" or "diplomacy" or "marker" or "open" or "loss" or "load" or "settlement")
            await Reject<IOException>(() => scenario.Run(verifyLoss), fault);
        else await Reject<AssertionException>(() => scenario.Run(verifyLoss), fault);
        Check(updates == 2 && current.GetRawText() == original.GetRawText(), "Raid failed to restore original configuration after " + fault);
    }
    private static object List(params object[] items) => new { items };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action, string context) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + context);
    }
}
