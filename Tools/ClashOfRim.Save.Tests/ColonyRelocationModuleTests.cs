using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ColonyRelocationModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var package = factory("relocation"); var culture = CultureInfo.CurrentCulture; int count = 0;
        void Case(Action action) { action(); count++; }
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (bool confirm in new[] { false, true })
                {
                    foreach (string mode in new[] { "success", "protocol", "identity", "snapshot-id", "negative-tile", "token", "missing-token", "wrong-user", "wrong-colony",
                        "deleted", "revoked", "replaced", "unconfigured", "missing-snapshot", "stale", "missing-site", "multiple-sites", "occupied", "nearby", "distance-two", "different-layer",
                        "prepare-revoked", "prepare-replaced", "prepare-deleted", "prepare-stale", "prepare-occupied", "prepare-failure",
                        "replay-revoked", "replay-deleted", "replay-stale", "replay-occupied", "replay-nearby", "replay-unconfigured" })
                        Case(() => Flow(package, confirm, mode));
                    foreach (var (status, blocks) in new[] { (ServerEventStatus.Recorded, true), (ServerEventStatus.ReadyForImmediateDelivery, true),
                        (ServerEventStatus.PendingOfflineDelivery, true), (ServerEventStatus.DeliveredToClient, true), (ServerEventStatus.Conflict, true),
                        (ServerEventStatus.AppliedToSnapshot, false), (ServerEventStatus.RejectedByTarget, false), (ServerEventStatus.Cancelled, false), (ServerEventStatus.Failed, false) })
                        Case(() => Pending(package, confirm, "item", status, "world", blocks));
                    foreach (var (kind, blocks) in new[] { ("support", true), ("market", false), ("memo", false), ("exchange", true) })
                        Case(() => Pending(package, confirm, kind, ServerEventStatus.Recorded, "map", blocks));
                    foreach (var (context, blocks) in new[] { ("tile", true), ("unbound", false), ("other", false), ("actor", false), ("other-colony", false) })
                        Case(() => Pending(package, confirm, "item", ServerEventStatus.Recorded, context, blocks));
                    foreach (bool actor in new[] { false, true })
                    foreach (bool active in new[] { false, true }) Case(() => Raid(package, confirm, actor, active));
                }
                foreach (string mode in new[] { "normal", "surface-zero", "wrong-tile", "wrong-layer", "none", "two-targets", "unique-target", "quest-active", "quest-inactive", "quest-no-landing", "empty-label" })
                    Case(() => Anchors(package, mode));
                foreach (string fault in new[] { "world", "awards" }) Case(() => Retry(package, fault));
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        foreach (bool confirm in new[] { false, true })
        {
            using var f = new HttpSnapshotRecoveryTests.Fixture(factory); f.Start(); f.Seed(); string before = f.Evidence();
            using var response = f.Post(confirm ? ProtocolMessageKind.ConfirmColonyRelocation : ProtocolMessageKind.PreflightColonyRelocation,
                confirm ? (object)new ConfirmColonyRelocationRequest(ProtocolApiVersion.Current, "receiver", "away", "before", "after", 23, "confirm", "invalid")
                    : new PreflightColonyRelocationRequest(ProtocolApiVersion.Current, "receiver", "away", "before", 23, "preflight", "invalid"));
            response.EnsureSuccessStatusCode();
            var result = response.Content.ReadFromJsonAsync<ColonyRelocationResponse>().GetAwaiter().GetResult()!;
            Check(!result.Result.Accepted && result.WorldConfiguration is null && result.OldSite is null, "Invalid HTTP request leaked world state.");
            f.AssertPending(before, confirm ? "Invalid relocation confirmation" : "Invalid relocation preflight"); count++;
        }
        foreach (bool confirm in new[] { false, true })
        {
            using var f = new HttpSnapshotRecoveryTests.Fixture(factory);
            ColonyDeletionStageTests.SeedWorld(f.State.WorldConfiguration);
            f.State.WorldConfiguration.RegisterPlayerColonySites("receiver", "away", [new("receiver", "away", "old", "Map_old", 12, "旧家")]);
            var identity = new SnapshotIdentity("receiver", "away", "after");
            f.Store.StoreLatest(new(identity, package.Envelope with { Identity = identity }, RelocatedIndex(package.Index), DateTimeOffset.UtcNow));
            f.Start(); f.Seed();
            f.Store.BeforePrepare = () => Check(!Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate), "HTTP relocation forced preparation under global gate.");
            using var response = f.Post(confirm ? ProtocolMessageKind.ConfirmColonyRelocation : ProtocolMessageKind.PreflightColonyRelocation,
                confirm ? (object)new ConfirmColonyRelocationRequest(ProtocolApiVersion.Current, "receiver", "away", "before", "after", 0, "confirm", f.Token, 2)
                    : new PreflightColonyRelocationRequest(ProtocolApiVersion.Current, "receiver", "away", "after", 0, "preflight", f.Token, 2));
            response.EnsureSuccessStatusCode();
            var result = response.Content.ReadFromJsonAsync<ColonyRelocationResponse>().GetAwaiter().GetResult()!;
            Check(result.Result.Accepted && (confirm ? result.NewSite?.Tile == 0 : result.OldSite?.Tile == 12), "Authorized HTTP relocation failed.");
            Check(f.State.WorldConfiguration.Current!.PlayerColonySites.Single(x => x.UserId == "receiver").Tile == (confirm ? 0 : 12), "HTTP preflight/confirmation published wrong location.");
            f.AssertRecovered(); count++;
        }
        Console.WriteLine($"Colony relocation independent module scenarios passed: {count}");
    }

    private static void Flow(SaveSnapshotPackage package, bool confirm, string mode)
    {
        var f = new Fixture(package);
        void Change(string change)
        {
            if (change == "revoked") f.S.Tokens.RevokeForUser("user");
            if (change == "replaced") { f.S.Sessions.EndUser("user"); f.S.Issue(); }
            if (change == "deleted") f.S.Players.MarkDeleted("user", "colony", null, DateTimeOffset.UtcNow);
            if (change == "stale") f.Store("newer");
            if (change == "unconfigured") f.World.Submit("admin", f.World.Current!);
            if (change == "occupied") f.World.RegisterPlayerColonySites("enemy", "colony", [Site("enemy", tile: 0, layer: 2)]);
            if (change == "nearby") f.Distances.Result = 1;
        }
        Change(mode);
        if (mode == "distance-two") f.Distances.Result = 2;
        if (mode == "different-layer") f.World.RegisterPlayerColonySites("enemy", "colony", [Site("enemy", tile: 0, layer: 1)]);
        if (mode == "missing-snapshot") f.S.Snapshots.RemoveLatest("user", "colony");
        if (mode == "missing-site") f.World.RemovePlayerColonySites("user", "colony");
        if (mode == "multiple-sites") f.World.RegisterPlayerColonySites("user", "colony", [Site(), Site(id: "second", tile: 40)]);
        string before = f.Evidence();
        f.S.Recovery.NeedsPreparation = true;
        f.S.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate), "Recovery prepared inside global gate.");
            if (mode.StartsWith("prepare-", StringComparison.Ordinal)) Change(mode[8..]);
            before = f.Evidence();
            if (mode == "prepare-failure") throw new IOException("Injected preparation failure.");
        };
        f.S.Recovery.OnReplay = () =>
        {
            Check(Monitor.IsEntered(f.S.Gate), "Recovery replay escaped mutation gate.");
            if (mode.StartsWith("replay-", StringComparison.Ordinal)) Change(mode[7..]);
            before = f.Evidence();
        };
        string user = mode == "identity" ? " " : mode == "wrong-user" ? "other" : "user";
        string colony = mode == "wrong-colony" ? "other" : "colony";
        string version = mode == "protocol" ? "bad" : ProtocolApiVersion.Current;
        string snapshot = mode == "snapshot-id" ? "" : "after";
        string? token = mode == "token" ? "bad" : mode == "missing-token" ? null : f.Token;
        ColonyRelocationResponse Call() => f.Call(confirm, version, user, colony, snapshot, mode == "negative-tile" ? -1 : 0, token);
        bool failed = false; ColonyRelocationResponse? result = null;
        try { result = Call(); } catch (IOException) { failed = true; }
        bool accepted = mode is "success" or "distance-two" or "different-layer" || confirm && mode is "missing-site" or "multiple-sites";
        Check(failed == (mode == "prepare-failure"), "Unexpected exception: " + mode);
        if (!failed) Check(result!.Result.Accepted == accepted, $"Unexpected acceptance {confirm}/{mode}: {result!.Result.Message}");
        Check(!Monitor.IsEntered(f.S.Gate), "Leaked mutation scope.");
        if (accepted && confirm) f.AssertMoved(result!);
        else Check(f.Evidence() == before, "Rejected/preflight/failed request changed world or awards: " + mode);
        if (mode is "protocol" or "identity" or "snapshot-id" or "negative-tile" or "token" or "missing-token" or "wrong-user" or "wrong-colony" or "deleted" or "revoked" or "replaced")
            Check(f.S.Recovery.Replays == 0 && result!.WorldConfiguration is null, "Invalid request recovered business or leaked world: " + mode);
        if (failed) { f.S.Recovery.OnPrepare = null; Check(Call().Result.Accepted, "Recovery failure could not retry."); }
    }

    private static void Anchors(SaveSnapshotPackage package, string mode)
    {
        var f = new Fixture(package); var index = f.Index; var obj = index.WorldObjects[0]; var map = index.Maps[0];
        int layer = mode == "surface-zero" ? 0 : 2;
        if (mode == "surface-zero") obj = obj with { Tile = "0,0" };
        if (mode == "wrong-tile") obj = obj with { Tile = "8,2" };
        if (mode == "wrong-layer") obj = obj with { Tile = "0,3" };
        if (mode == "empty-label") obj = obj with { Name = " " };
        if (mode.StartsWith("quest-", StringComparison.Ordinal))
        {
            obj = obj with { Class = "Site", Def = "Site" }; map = map with { WasSpawnedViaGravshipLanding = mode != "quest-no-landing" };
            if (mode != "quest-inactive") f.Baseline.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [new ModManifestEntry { PackageId = OdysseyCompatibilityKeys.PackageId }] }, "admin", DateTimeOffset.UtcNow);
        }
        index = index with { Maps = mode == "none" ? [] : [map], WorldObjects = [obj] };
        if (mode is "two-targets" or "unique-target") index = index with
        {
            Maps = [map, map with { UniqueId = "24", ParentWorldObjectId = "second" }],
            WorldObjects = [obj, obj with { Id = "second", UniqueLoadId = "second", Tile = mode == "two-targets" ? "0,2" : "90,2" }]
        };
        f.Store(index: index); string before = f.Evidence();
        var result = f.Call(true, layer: layer);
        bool accepted = mode is "normal" or "surface-zero" or "unique-target" or "quest-active" or "empty-label";
        Check(result.Result.Accepted == accepted, "Wrong anchor acceptance: " + mode);
        if (accepted)
        {
            f.AssertMoved(result, layer);
            Check(result.NewSite!.Label == (mode == "empty-label" ? "colony" : "新家"), "Lost label or fallback.");
        }
        else Check(before == f.Evidence(), "Invalid snapshot anchor changed world.");
    }

    private static void Pending(SaveSnapshotPackage package, bool confirm, string kind, ServerEventStatus status, string context, bool blocks)
    {
        var f = new Fixture(package);
        LedgerEventPayload payload = kind switch
        {
            "support" => new SupportPawnEventPayload("pawn", "source", "pawn", true, null),
            "market" or "memo" or "exchange" => new TradeEventPayload("trade", kind == "market" ? TradeStage.MarketOrder : kind == "memo" ? TradeStage.AcceptedMemo : TradeStage.SelfDeliveryExchange, [], [], 0),
            _ => new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "source")], "gift")
        };
        var type = payload is TradeEventPayload ? ServerEventType.Trade : payload is SupportPawnEventPayload ? ServerEventType.SupportPawn : ServerEventType.ItemDelivery;
        var target = new EventParty(context == "actor" ? "other" : "user", context == "other-colony" ? "other" : "colony");
        EventTargetContext? binding = context == "unbound" ? null : new(context is "world" or "actor" or "other-colony" ? "old" : "elsewhere", context == "map" ? "Map_old" : null,
            context == "tile" ? 12 : 91, EventLandingMode.MapEdge);
        f.S.Ledger.Append(AuthoritativeEventFactory.Create(type, new(context == "actor" ? "user" : "sender", "colony"), target, "pending", false, payload, DateTimeOffset.UtcNow, binding) with { Status = status });
        string before = f.Evidence(); var result = f.Call(confirm);
        Check(result.Result.Accepted != blocks, $"Event binding {confirm}/{kind}/{status}/{context}.");
        if (blocks || !confirm) Check(before == f.Evidence(), "Blocked/preflight event mutated world.");
    }

    private static void Raid(SaveSnapshotPackage package, bool confirm, bool actor, bool active)
    {
        var f = new Fixture(package);
        f.S.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Raid,
            new(actor ? "user" : "enemy", "colony"), new(actor ? "enemy" : "user", "colony"), "raid", false,
            new RaidEventPayload("defender", active ? null : "returned", DateTimeOffset.UtcNow, null, null, new("attacker", [], [])), DateTimeOffset.UtcNow));
        Check(f.Call(confirm).Result.Accepted != active, $"Raid participant {confirm}/{actor}/{active}.");
    }

    private static void Retry(SaveSnapshotPackage package, string fault)
    {
        var f = new Fixture(package);
        f.Awards.RecordRelocation("user", "colony", "one", DateTimeOffset.UtcNow);
        f.Awards.RecordRelocation("user", "colony", "two", DateTimeOffset.UtcNow);
        string before = f.Evidence();
        f.Slot.Before = _ => Check(Monitor.IsEntered(f.S.Gate), "Confirmation saved outside mutation boundary.");
        f.Slot.Fail = fault == "world"; f.AwardSlot.Fail = fault == "awards";
        bool failed = false;
        try { f.Call(true); } catch (IOException) { failed = true; }
        Check(failed && !Monitor.IsEntered(f.S.Gate), "Fault missed or scope leaked.");
        if (fault == "world") Check(f.Evidence() == before, "Failed world save published candidate state.");
        else Check(f.Reopen().Current!.PlayerColonySites.Single(x => x.UserId == "user").Tile == 0, "Independent world commit lost after award failure.");
        f.Slot.Fail = f.AwardSlot.Fail = false;
        f.Recreate(); f.AssertMoved(f.Call(true)); f.AssertMoved(f.Call(true));
        Check(new AchievementRegistry(f.AwardSlot).ListAggregates().Single() is { AchievementId: "relocations_3", Value: 3, SourceSnapshotId: "after" }
            && f.S.Ledger.ListAll().Count == 1, "Confirmation retry duplicated/lost relocation award.");
    }

    private static PlayerColonySiteDto Site(string user = "user", string id = "old", int tile = 12, int layer = 0)
        => new(user, "colony", id, "Map_old", tile, "旧家", "原阵营", new("custom", "icon", "color", "#AABBCC"), layer);

    private static SaveSnapshotIndex RelocatedIndex(SaveSnapshotIndex source) => source with
    {
        Maps = [new("23", null, "object", null, false, false, false, false, 0, 0, PlayerColonistCount: 1)],
        WorldObjects = [new("raw-object", "object", "Settlement", "Settlement", "0,2", "Faction_7", "新家", false)],
        Factions = [new("7", "player", "PlayerColony", null, null, false, false, false)]
    };

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly RecoveryTestSupport.Slot Slot = new(), AwardSlot = new();
        internal readonly WorldConfigurationPersistenceTests.MemoryBinary Binary = new();
        internal readonly CompatibilityBaselineRegistry Baseline = new();
        internal readonly EventNotificationHub Notifications = new();
        internal readonly Distances Distances = new();
        internal readonly ServerPluginRegistry Plugins = new ServerPluginRegistry().WithBuiltInPlugins([OdysseyServerCompatibility.Descriptor]);
        internal readonly SaveSnapshotPackage Package;
        internal readonly SaveSnapshotIndex Index;
        internal readonly string Token;
        internal WorldConfigurationRegistry World = null!;
        internal ColonyRelocation Module = null!;
        internal AchievementAwards Awards = null!;
        internal Fixture(SaveSnapshotPackage package)
        {
            Package = package; Token = S.Issue().Token;
            Index = RelocatedIndex(package.Index);
            Recreate(); ColonyDeletionStageTests.SeedWorld(World); World.RegisterPlayerColonySites("user", "colony", [Site()]);
            S.Players.Record("watcher", "other", null, DateTimeOffset.UtcNow); Store();

        }
        internal void Recreate()
        {
            World = Reopen(); var relations = new DiplomacyRelationRegistry(); var delivery = new WorldConfigurationDelivery(Plugins, Baseline);
            var raids = new RaidMarkerProjection(S.Ledger, S.Snapshots);
            var markers = new WorldMarkerDelivery(S.Players, S.Presence, relations, S.Snapshots, World, delivery, new(), raids,
                new RaidProtection(S.Ledger, new(), new(), () => S.Configuration, NullLogger.Instance), new(S.Players, S.Snapshots), () => S.Configuration);
            Awards = new(new(AwardSlot), Plugins, Baseline, S.Ledger, S.Presence, S.Events, () => NullLogger.Instance);
            Module = new(new(new(new(S.Tokens, S.Sessions), relations, new(), S.Ledger, () => S.Configuration.PvpEnabled), S.Players), S.Mutations,
                S.Snapshots, World, new(Plugins, Baseline), Distances, raids, S.Ledger, Plugins, Baseline, Awards, delivery, markers,
                new(S.Players, Notifications), () => NullLogger.Instance);
        }
        internal WorldConfigurationRegistry Reopen() => new(Slot, Binary, Plugins.ActiveWorldConfigurationExtensions(Baseline.Current));
        internal void Store(string id = "after", SaveSnapshotIndex? index = null)
        {
            var identity = new SnapshotIdentity("user", "colony", id);
            S.Snapshots.StoreLatest(new(identity, Package.Envelope with { Identity = identity }, index ?? Index, DateTimeOffset.UtcNow));
        }
        internal ColonyRelocationResponse Call(bool confirm, string version = ProtocolApiVersion.Current, string user = "user", string colony = "colony",
            string snapshot = "after", int tile = 0, string? token = "fixture-token", int layer = 2)
            => confirm ? Module.Confirm(new(version, user, colony, "before", snapshot, tile, "request", token == "fixture-token" ? Token : token, layer))
                : Module.Preflight(new(version, user, colony, snapshot, tile, "request", token == "fixture-token" ? Token : token, layer));
        internal string Evidence() => JsonSerializer.Serialize(new { Current = World.Current, Disk = Reopen().Current,
            WorldNotice = Notifications.GetVersion("user"), WatcherNotice = Notifications.GetVersion("watcher"), Awards = AwardSlot.Read(), Events = S.Ledger.ListAll() });
        internal void AssertMoved(ColonyRelocationResponse response, int layer = 2)
        {
            Check(response.Result.Accepted && response.NewSite is { Tile: 0, MapUniqueId: "Map_23", WorldObjectId: "object" } site && site.TileLayerId == layer,
                "Missing accepted anchor.");
            Check(response.WorldMapMarkers is not null && response.WorldConfiguration is not null && Notifications.GetVersion("user") > 0
                && Notifications.GetVersion("watcher") > 0, "Missing delivery/notifications.");
            var stored = Reopen().Current!.PlayerColonySites.Single(x => x.UserId == "user");
            Check(stored.Tile == 0 && stored.TileLayerId == layer && stored.ColonyId == "colony", "World move not durable.");
            if (response.OldSite is not null) Check(response.NewSite!.FactionName == response.OldSite.FactionName && response.NewSite.Appearance == response.OldSite.Appearance, "Lost old display data.");
            Check(stored.FactionName == response.NewSite!.FactionName && JsonSerializer.Serialize(stored.Appearance) == JsonSerializer.Serialize(response.NewSite.Appearance), "Display data not persisted.");
            Check(Reopen().Current!.PlayerColonySites.Count(x => x.UserId is "owner" or "other-user") == 4, "Move removed foreign colonies.");
        }
    }
    private sealed class Distances : IWorldTileDistanceCalculator
    {
        internal int? Result;
        public int? TryCalculateDistance(WorldTileGeometryDistanceSource? geometry, WorldTileRef fromTile, WorldTileRef toTile, int crossLayerOverheadDistanceTiles) => Result;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Colony relocation: " + message); }
}
