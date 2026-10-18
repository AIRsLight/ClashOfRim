using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static AIRsLight.ClashOfRim.Network.Modules.World.ColonySiteRules;

internal static class ColonySiteModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> build)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var package = build("colony-projection");
        var culture = CultureInfo.CurrentCulture;
        int count = 0; void Case(Action action) { action(); count++; }
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Case(Tiles); Case(Rules); Case(ExtensionEquality);
                foreach (string mode in new[] { "normal", "colonists", "tribe", "load-id", "prefixed-id", "enemy", "destroyed", "missing", "no-parent", "invalid-tile", "duplicate", "gravship", "no-landing", "no-evidence" })
                    Case(() => Anchors(package.Index, mode));
                foreach (string mode in new[] { "same", "world-id", "map-id", "different", "raw-map-id", "missing" })
                foreach (bool relocation in new[] { false, true }) Case(() => Continuity(package, mode, relocation));
                foreach (string mode in new[] { "new", "update", "move", "layer-move", "occupied", "other-colony", "missing", "multiple", "extensions", "unchanged", "rejected", "unconfigured", "persistence-failure" })
                    Case(() => Projection(package, mode));
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"Colony site independent module scenarios passed: {count}");
    }

    private static SaveSnapshotIndex Index(SaveSnapshotIndex source) => source with
    {
        Maps = [new("23", null, "object", null, false, false, false, false, 0, 0)],
        WorldObjects = [new("raw-object", "object", "Settlement", "Settlement", "0,2", "Faction_7", "新家", false)],
        Factions = [new("7", "player", "PlayerColony", null, null, false, false, false)]
    };

    private static void Anchors(SaveSnapshotIndex source, string mode)
    {
        var index = Index(source); var map = index.Maps[0]; var obj = index.WorldObjects[0];
        if (mode is "enemy" or "colonists" or "no-evidence") obj = obj with { Faction = "enemy" };
        if (mode == "colonists") map = map with { PlayerColonistCount = 1 };
        if (mode == "tribe") index = index with { Factions = [index.Factions[0] with { Def = "PlayerTribe" }] };
        if (mode == "load-id") obj = obj with { Faction = "7", UniqueLoadId = null, Id = "object" };
        if (mode == "prefixed-id") map = map with { UniqueId = "Map_23" };
        if (mode == "destroyed") obj = obj with { Destroyed = true };
        if (mode == "missing") map = map with { ParentWorldObjectId = "absent" };
        if (mode == "no-parent") map = map with { ParentWorldObjectId = null };
        if (mode == "invalid-tile") obj = obj with { Tile = "0,-2" };
        bool quest = mode is "gravship" or "no-landing" or "no-evidence";
        if (quest) { obj = obj with { Class = "Site", Def = "Site" }; map = map with { WasSpawnedViaGravshipLanding = mode != "no-landing" }; }
        index = index with { Maps = mode == "duplicate" ? [map, map with { UniqueId = "later" }] : [map], WorldObjects = [obj] };
        string before = JsonSerializer.Serialize(index);
        var plugins = new ServerPluginRegistry().WithBuiltInPlugins([OdysseyServerCompatibility.Descriptor]);
        var baseline = new CompatibilityBaselineRegistry(); var locations = new SnapshotColonyLocations(plugins, baseline);
        bool ordinary = mode is "normal" or "colonists" or "tribe" or "load-id" or "prefixed-id" or "duplicate";
        Check(locations.Extract(index).Count == (ordinary ? 1 : 0), "Inactive DLC or invalid anchor accepted: " + mode);
        baseline.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [new ModManifestEntry { PackageId = OdysseyCompatibilityKeys.PackageId }] }, "admin", DateTimeOffset.UnixEpoch);
        var actual = locations.Extract(index);
        Check(actual.Count == (ordinary || mode == "gravship" ? 1 : 0), "Wrong active anchor: " + mode);
        if (actual.Count == 1)
        {
            var raw = PlayerColonyAnchors.Extract(index, [static _ => true]).Single();
            Check(raw.MapUniqueId == map.UniqueId && actual[0] == raw with { MapUniqueId = "Map_23" }
                && actual[0].Tile == 0 && actual[0].TileLayerId == 2, "Map identity or zero/layer changed: " + mode);
        }
        baseline.ReplaceBaseline(EntryTestSupport.Manifest(), "admin", DateTimeOffset.UnixEpoch);
        Check(locations.Extract(index).Count == (ordinary ? 1 : 0) && JsonSerializer.Serialize(index) == before, "Stale DLC selection or mutated index.");
    }

    private static void Tiles()
    {
        foreach (var (text, valid, tile, layer) in new (string?, bool, int, int)[]
        { ("0", true, 0, 0), (" +23 , +2 ", true, 23, 2), ("2147483647,0", true, int.MaxValue, 0),
          (null, false, 0, 0), ("", false, 0, 0), ("1,", false, 0, 0), ("-1", false, 0, 0),
          ("1,-2", false, 0, 0), ("1,2,3", false, 0, 0), ("2147483648", false, 0, 0), ("١,٢", false, 0, 0) })
        {
            Check(WorldTileText.TryParse(text, out int parsedTile, out int parsedLayer) == valid, "Tile acceptance: " + text);
            Check(WorldTileGeometry.TryParsePlanetTile(text, out var reference) == valid, "Adapter parse diverged.");
            if (valid) Check(parsedTile == tile && parsedLayer == layer && reference == new WorldTileRef(tile, layer), "Tile coordinates changed.");
        }
        var previous = CultureInfo.CurrentCulture; var custom = (CultureInfo)previous.Clone(); custom.NumberFormat.PositiveSign = "p";
        try { CultureInfo.CurrentCulture = custom; Check(WorldTileText.TryParse("+23,+2", out _, out _) && !WorldTileText.TryParse("p23,p2", out _, out _), "Tile format depends on process culture."); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Continuity(SaveSnapshotPackage package, string mode, bool relocation)
    {
        var expected = Index(package.Index); var map = expected.Maps[0]; var obj = expected.WorldObjects[0];
        if (mode is "map-id" or "different" or "raw-map-id") { obj = obj with { UniqueLoadId = "changed", Id = "changed" }; map = map with { ParentWorldObjectId = "changed" }; }
        if (mode is "world-id" or "different") map = map with { UniqueId = "changed" };
        if (mode == "raw-map-id") map = map with { UniqueId = "Map_23" };
        var index = expected with { Maps = mode == "missing" ? [] : [map], WorldObjects = [obj] };
        var store = new InMemoryColonySnapshotIndexStore();
        var oldIdentity = package.Envelope.Identity with { SnapshotId = "previous" };
        store.StoreLatest(new(oldIdentity, package.Envelope with { Identity = oldIdentity, OriginalSha256 = "previous-content" }, expected, DateTimeOffset.UnixEpoch));
        var receiver = new SnapshotUploadReceiver(store);
        var result = receiver.CommitPrepared(new(package.Envelope.Identity.OwnerId!, package.Envelope.Identity.ColonyId!,
            package.Envelope.Identity.SnapshotId!, relocation ? "ColonyRelocation" : null), package,
            SnapshotUploadResult.Accept(new(package.Envelope.Identity, package.Envelope, index, DateTimeOffset.UnixEpoch)), storeAccepted: false);
        bool accepted = mode is "same" or "world-id" or "map-id" || relocation && mode != "missing";
        Check(result.Accepted == accepted, $"Continuity {mode}/{relocation}: {result.Kind} {result.Message}");
    }

    private static PlayerColonySiteDto Site(string user = "owner", string colony = "colony", int tile = 0, int layer = 2, string id = "object", string? label = null)
        => new(user, colony, id, "Map_23", tile, label ?? "original", "faction", tileLayerId: layer);

    private static void Rules()
    {
        var own = Site(); var other = Site("other"); var next = Site(tile: 1); var lower = Site("other", layer: 0);
        var normalized = NormalizePlayerColonySites("owner", "colony", [Site("fake", "fake", label: "first"), Site("fake", "fake", label: "last"), Site(tile: -1), Site(tile: 2, id: "two")]);
        Check(normalized.Count == 2 && normalized[0].Label == "last" && normalized.All(x => x.UserId == "owner" && x.ColonyId == "colony"), "Site ownership/dedup/sort changed.");
        Check(FindSameTilePlayerColonyConflict([own, lower, other], [own], "owner", "colony") == other, "Same tile/layer conflict or owner exclusion changed.");
        Check(FindExistingUserColonyConflict([own, other], [own], "owner", "colony") is null
            && FindExistingUserColonyConflict([Site(colony: "other")], [own], "owner", "colony") is not null, "Single active colony rule changed.");
        Check(FindExistingSameColonySiteOnDifferentTile([own, next], [own], "owner", "colony") is null
            && FindExistingSameColonySiteOnDifferentTile([own], [Site(layer: 3)], "owner", "colony") == own, "First identity or cross-layer relocation rule changed.");
        var world = new WorldConfigurationRegistry(); ColonyDeletionStageTests.SeedWorld(world);
        var distances = new Distances();
        foreach (int? distance in new int?[] { null, 0, 1, 2 })
        {
            distances.Result = distance; distances.Calls = 0;
            var conflict = FindNearbyPlayerColonyConflict(world.Current, distances, [own, Site(tile: -1), other], [own], "owner", "colony");
            Check((conflict == other) == (distance == 1) && distances.Calls == 1, "Nearby distance policy changed.");
        }
        distances.Calls = 0;
        Check(FindNearbyPlayerColonyConflict(null, distances, [other], [own], "owner", "colony") is null && distances.Calls == 0, "Missing geometry invoked distance.");
    }

    private static void Projection(SaveSnapshotPackage package, string mode)
    {
        var provider = new ProjectionProvider(); var plugins = new ServerPluginRegistry().WithBuiltInPlugins([
            new("test.colony", "test", "1", "test", "", [], WorldConfigurationExtensionProviders: [provider])]);
        var baseline = new CompatibilityBaselineRegistry(); var slot = new RecoveryTestSupport.Slot(); var binary = new WorldConfigurationPersistenceTests.MemoryBinary();
        WorldConfigurationRegistry Open() => new(slot, binary, plugins.ActiveWorldConfigurationExtensions(baseline.Current));
        var world = Open(); var players = new PlayerRegistry(); var notifications = new EventNotificationHub();
        players.Record("owner", "colony", null, DateTimeOffset.UnixEpoch); players.Record("watcher", "colony", null, DateTimeOffset.UnixEpoch);
        if (mode != "unconfigured")
        {
            ColonyDeletionStageTests.SeedWorld(world); world.RemovePlayerColonySites("owner", "abandoned"); world.RemovePlayerColonySites("owner", "other-colony");
            if (mode is "update" or "move" or "layer-move") world.RegisterPlayerColonySites("owner", "colony", [Site()]);
            if (mode == "occupied") world.RegisterPlayerColonySites("enemy", "colony", [Site("enemy")]);
            if (mode == "other-colony") world.RegisterPlayerColonySites("owner", "another", [Site(colony: "another")]);
        }
        var index = Index(package.Index);
        if (mode == "move") index = index with { WorldObjects = [index.WorldObjects[0] with { Tile = "1,2" }] };
        if (mode == "layer-move") index = index with { WorldObjects = [index.WorldObjects[0] with { Tile = "0,3" }] };
        if (mode is "missing" or "extensions" or "unchanged") index = index with { Maps = [] };
        if (mode == "multiple") index = index with { Maps = [index.Maps[0], index.Maps[0] with { UniqueId = "24", ParentWorldObjectId = "second" }], WorldObjects = [index.WorldObjects[0], index.WorldObjects[0] with { UniqueLoadId = "second", Id = "second" }] };
        if (mode == "extensions") provider.Incoming = [Extension("new")];
        var projection = new SnapshotColonyProjection(world, plugins, baseline, new(plugins, baseline), new(players, notifications));
        var upload = mode == "rejected" ? SnapshotUploadResult.Reject(SnapshotUploadResultKind.InvalidPayload, "test")
            : SnapshotUploadResult.Accept(new(package.Envelope.Identity, package.Envelope, index, DateTimeOffset.UnixEpoch));
        string before = JsonSerializer.Serialize(world.Current); int writes = slot.Writes;
        slot.Fail = mode == "persistence-failure"; bool failed = false;
        try { projection.Apply("owner", "colony", upload); } catch (IOException) { failed = true; }
        Check(failed == (mode == "persistence-failure"), "Wrong projection failure: " + mode);
        bool changes = mode is "new" or "update" or "extensions";
        Check(notifications.GetVersion("owner") == (changes ? 1 : 0) && notifications.GetVersion("watcher") == (changes ? 1 : 0), "Wrong projection notification: " + mode);
        if (!changes) Check(JsonSerializer.Serialize(world.Current) == before && JsonSerializer.Serialize(Open().Current) == before && slot.Writes == writes, "Skipped/failed projection mutated state: " + mode);
        if (failed) { slot.Fail = false; projection.Apply("owner", "colony", upload); changes = true; Check(notifications.GetVersion("owner") == 1, "Retry notification missing."); }
        if (changes)
        {
            var stored = Open().Current!;
            if (mode == "extensions") Check(stored.Extensions.Single().PayloadJson == "new" && stored.PlayerColonySites.All(x => x.UserId != "owner"), "Extension-only publication changed sites.");
            else
            {
                var site = stored.PlayerColonySites.Single(x => x.UserId == "owner" && x.ColonyId == "colony");
                Check(site is { Tile: 0, TileLayerId: 2, MapUniqueId: "Map_23", Label: "新家" }
                    && site.FactionName == (mode == "update" ? "faction" : null), "Published site identity or existing faction lost.");
            }
            Check(stored.PlayerColonySites.Any(x => x.UserId == "other-user"), "Unrelated site removed.");
        }
    }

    private static WorldConfigurationExtensionDto Extension(string payload, string kind = "kind", string version = "1", IReadOnlyDictionary<string, string?>? metadata = null)
        => new("test.colony", kind, version, payload, metadata);

    private static void ExtensionEquality()
    {
        var left = new[] { Extension("one", metadata: new Dictionary<string, string?> { ["I"] = "1", ["b"] = null }), Extension("two", "other") };
        Check(WorldExtensionValues.Equal(null, []) && WorldExtensionValues.Equal(left, [left[1], Extension("one", metadata: new Dictionary<string, string?> { ["b"] = null, ["I"] = "1" })]), "Ordering affects extension equality.");
        foreach (var changed in new[] { Extension("changed"), Extension("one", "OTHER"), Extension("one", version: "2"), Extension("one", metadata: new Dictionary<string, string?> { ["i"] = "1", ["b"] = null }) })
            Check(!WorldExtensionValues.Equal(left, [changed, left[1]]), "Changed extension considered equal.");
    }

    private sealed class ProjectionProvider : IWorldConfigurationExtensionProvider
    {
        internal IReadOnlyList<WorldConfigurationExtensionDto> Incoming = [];
        public IReadOnlyList<WorldConfigurationExtensionKey> HandledKeys => [new("test.colony", "kind")];
        public IReadOnlyList<WorldConfigurationExtensionDto> NormalizeSubmittedExtensions(WorldConfigurationExtensionContext context, IReadOnlyList<WorldConfigurationExtensionDto> incoming) => incoming;
        public IReadOnlyList<WorldConfigurationExtensionDto> MergeExtensions(WorldConfigurationExtensionContext context, IReadOnlyList<WorldConfigurationExtensionDto> current, IReadOnlyList<WorldConfigurationExtensionDto> incoming) => incoming.Count == 0 ? current : incoming;
        public IReadOnlyList<WorldConfigurationExtensionDto> RemoveColonyExtensions(string userId, string colonyId, IReadOnlyList<WorldConfigurationExtensionDto> current) => current;
        public IReadOnlyList<WorldConfigurationExtensionDto> BuildDeliveryExtensions(IReadOnlyList<WorldConfigurationExtensionDto> current) => current;
        public IReadOnlyList<WorldConfigurationExtensionDto> BuildExtensionsFromAcceptedSnapshot(WorldConfigurationExtensionSnapshotContext context) => Incoming;
    }

    private sealed class Distances : IWorldTileDistanceCalculator
    {
        internal int? Result; internal int Calls;
        public int? TryCalculateDistance(WorldTileGeometryDistanceSource? geometry, WorldTileRef fromTile, WorldTileRef toTile, int crossLayerOverheadDistanceTiles)
        { Check(geometry is not null && fromTile == new WorldTileRef(0, 2) && toTile == new WorldTileRef(0, 2) && crossLayerOverheadDistanceTiles == 0, "Distance context changed."); Calls++; return Result; }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Colony sites: " + message); }
}
