using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorldMarkerModuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10);
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "tagged", "tagged-with-legacy", "tagged-with-observer", "legacy", "foreign", "ambiguous", "ambiguous-tagged", "observer", "scout", "destroyed", "missing-map" })
                    BattleBinding(factory, mode);
                Delivery(factory); Configuration(); SiteIdentity();
            }
            Wealth();
            RaidVisibility();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void BattleBinding(Func<string, SaveSnapshotPackage> factory, string mode)
    {
        var f = new Fixture(); f.Ledger.Append(RaidReadModuleTests.Raid("current") with
            { TargetContext = new("defender-object", "defender-map", 12, EventLandingMode.MapEdge) });
        var current = Object("current-object", mode == "legacy" ? "" : "current");
        if (mode == "foreign") current = current with { ClashOfRimRelatedEventId = "foreign" };
        if (mode == "observer") current = current with { Def = "ClashOfRim_RemoteRaidObservationMapParent" };
        if (mode == "scout") current = current with { Def = "ClashOfRim_RemoteScoutMapParent" };
        if (mode == "destroyed") current = current with { Destroyed = true };
        var extra = Object("extra-object", mode is "ambiguous" or "tagged-with-legacy" ? "" : "previous");
        if (mode == "ambiguous-tagged") extra = extra with { ClashOfRimRelatedEventId = "current" };
        if (mode == "tagged-with-observer") extra = extra with { ClashOfRimRelatedEventId = "current", Def = "ClashOfRim_RemoteRaidObservationMapParent" };
        if (mode == "ambiguous") current = current with { ClashOfRimRelatedEventId = "" };
        var package = factory("marker-binding");
        var index = package.Index with
        {
            WorldObjects = [extra, current],
            Maps = mode == "missing-map" ? [Map("old-map", "extra-object")] : [Map("old-map", "extra-object"), Map("current-map", "current-object")]
        };
        f.Store(package, "attacker", "attack-colony", "latest", index);
        var marker = f.Markers.Build("attacker", "attack-colony", Now).Markers.Single();
        bool resolved = mode is "tagged" or "tagged-with-legacy" or "tagged-with-observer" or "legacy";
        Check(marker.MapUniqueId == (resolved ? "current-map" : "defender-map")
            && marker.WorldObjectId == (resolved ? "current-object" : "defender-object")
            && marker.SnapshotId == "latest" && marker.RelatedEventId == "current", "Active raid selected a foreign/ambiguous map or lost fallback: " + mode);
    }

    private static void Delivery(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new Fixture(); ColonyDeletionStageTests.SeedWorld(f.World);
        f.World.RegisterPlayerColonySites("layer-owner", "layer-colony", [new("layer-owner", "layer-colony", null, null, 2, "orbit", tileLayerId: 2)]);
        var package = factory("marker-owner");
        f.Store(package, "owner", "abandoned", "owner-snapshot", package.Index with
            { Maps = [Map("1", "1") with { WealthTotal = 200000 }, Map("2", "2")], HistoryWealthItems = null, HistoryWealthBuildings = null, HistoryWealthPawns = null });
        f.Players.Record("owner", "abandoned", "owner-snapshot", Now.AddMinutes(-1));
        Check(f.Presence.TryConnectExclusive("owner", "presence", out var online), "Owner did not connect.");
        using var lease = online;
        f.Relations.SetRelationKind("viewer", "view-colony", "owner", "abandoned", DiplomacyRelationRegistry.RelationAlly, null, Now);
        f.Runtime.ReplaceForOwner("roamer", "travel", "travel-snapshot", [new("caravan", "Caravan", "Caravan", 3, "travel", [1, 2, 3], 2)], Now);
        f.Runtime.ReplaceForOwner("viewer", "view-colony", "own", [new("own", "Caravan", "Caravan", 1, "own")], Now);
        var result = f.Markers.Build("viewer", "view-colony", Now);
        var own = result.Markers.Where(m => m.OwnerUserId == "owner" && m.ColonyId == "abandoned").ToArray();
        Check(own.Length == 2 && own.All(m => m.ReinforcementEnabled && m.TradeEnabled && m.RaidAvailability is not null
            && m.SnapshotId == "owner-snapshot" && m.OwnerOnline && m.OwnerLastSeenAtUtc == Now.AddMinutes(-1)
            && m.RelationKind == DiplomacyRelationRegistry.RelationAlly), "Baseline marker context or ally support changed.");
        var caravan = result.Markers.Single(m => m.OwnerUserId == "roamer");
        Check(caravan.TileLayerId == 2 && caravan.PathTiles!.SequenceEqual(new[] { 1, 2, 3 })
            && result.Markers.Single(m => m.OwnerUserId == "layer-owner").TileLayerId == 2
            && result.Markers.All(m => m.OwnerUserId != "viewer"), "Runtime/site layers, path or self exclusion changed.");
        Check(result.Markers.SequenceEqual(result.Markers.OrderBy(m => m.Tile).ThenBy(m => m.Kind).ThenBy(m => m.MarkerId, StringComparer.Ordinal)), "Marker ordering changed.");
        f.Config = new(giftsEnabled: false, pvpEnabled: false);
        f.Relations.SetRelationKind("viewer", "view-colony", "owner", "abandoned", DiplomacyRelationRegistry.RelationHostile, null, Now);
        result = f.Markers.Build("viewer", "view-colony", Now.AddSeconds(90));
        Check(!result.GiftsEnabled && !result.PvpEnabled && result.Markers.All(m => m.OwnerUserId != "roamer")
            && result.Markers.Where(m => m.OwnerUserId == "owner" && m.ColonyId == "abandoned").All(m => !m.ReinforcementEnabled && m.RelationKind == DiplomacyRelationRegistry.RelationHostile),
            "Delivery cached expired markers or old configuration/relations.");
        var raid = RaidReadModuleTests.Raid("viewer-raid", "viewer") with { Actor = new("viewer", "view-colony"), TargetContext = new("target", "map", 9, EventLandingMode.MapEdge) };
        f.Ledger.Append(raid);
        result = f.Markers.Build("viewer", "view-colony", Now);
        Check(result.Markers.Count == 1 && result.Markers[0].RelatedEventId == raid.EventId, "Active attacker received unrelated world destinations.");
        Check(f.Markers.Build("defender", "defend-colony", Now).Markers.All(m => m.RelatedEventId != raid.EventId), "Defender received its own attacking-player marker.");
    }

    private static WorldConfigurationDto Source(IReadOnlyList<PlayerColonySiteDto> sites)
        => new("world", "admin", "colony", Now, "seed", "1.0", "High", "Low", "Normal", "High", "100", ["Faction"],
            [new("mountain", "mountain", 1, 0, 1, 0)], [], [], [], sites, "Cassandra", "Custom", new([]),
            "<saveable><threatScale>2.35</threatScale></saveable>",
            [CoreRaidDifficultyServerCompatibility.BuildBaselineExtension(new("Cassandra", "Custom", "2.35", "1.5", "100", []))!],
            "German", [new("French", "world", [new("mountain", "montagne", 1, 0, 1, 0)])]);

    private static void Configuration()
    {
        var compatibility = new CompatibilityBaselineRegistry();
        var plugin = CoreRaidDifficultyServerCompatibility.Descriptor with { RequiredPackageIds = ["test.world"] };
        var module = new WorldConfigurationDelivery(new ServerPluginRegistry().WithBuiltInPlugins([plugin]), compatibility);
        var source = Source([new("owner", "colony", "world", "map", 5, "first"), new("owner", "colony", "world", "map2", 6, "last"), new("bad", "colony", "bad", null, -1, "invalid")]);
        string before = JsonSerializer.Serialize(source);
        Check(module.Build(null) is null && module.Build(source)!.Extensions.Count == 0, "Unavailable plugin was activated.");
        compatibility.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [new ModManifestEntry { PackageId = "test.world" }] }, "admin", Now);
        foreach (bool generation in new[] { false, true })
        foreach (bool sites in new[] { false, true })
        foreach (bool extensions in new[] { false, true })
        {
            var delivered = module.Build(source, generation, sites, extensions)!;
            Check(delivered.WorldConfigurationId == "world" && delivered.ConfiguredByUserId == "admin" && delivered.ConfiguredAtUtc == Now
                && delivered.DifficultyValuesXml == source.DifficultyValuesXml && delivered.StorytellerDefName == "Cassandra"
                && delivered.DifficultyDefName == "Custom" && delivered.GameLanguage == "German" && delivered.TileGeometry is null,
                "Lightweight delivery discarded runtime difficulty/language or delivered geometry.");
            Check(delivered.SeedString == (generation ? "seed" : null) && delivered.PlanetCoverage == (generation ? "1.0" : null)
                && delivered.Features.Count == (generation ? 1 : 0) && delivered.FeatureNameCatalogs.Count == (generation ? 1 : 0)
                && delivered.PlayerColonySites.Count == (sites ? 1 : 0) && delivered.Extensions.Count == (generation && extensions ? 1 : 0), "Delivery flags or deduplication diverged.");
            if (sites) Check(delivered.PlayerColonySites.Single().Label == "last", "Latest site identity did not win.");
        }
        compatibility.ReplaceBaseline(EntryTestSupport.Manifest("disabled"), "admin", Now);
        Check(module.Build(source)!.Extensions.Count == 0 && JsonSerializer.Serialize(source) == before, "Delivery mutated input or cached active plugins after baseline change.");
    }

    private static void SiteIdentity()
    {
        var sites = new[] { new PlayerColonySiteDto("u\u001fx", "c", "site", null, 1, "one"), new("u", "x\u001fc", "site", null, 1, "two"),
            new("u\nx", "c", "site", null, 1, "three"), new("u", "x\nc", "site", null, 1, "four"),
            new("u", "c", null, null, 1, "surface"), new("u", "c", null, null, 1, "orbit", tileLayerId: 2) };
        Check(WorldColonySites.Current(Source(sites)).Count == sites.Length, "Site identity conflated a delimiter or tile layer.");
        var world = new WorldConfigurationRegistry(WorldConfigurationExtensionService.Empty);
        ColonyDeletionStageTests.SeedWorld(world);
        foreach (var group in sites.GroupBy(site => (site.UserId, site.ColonyId)))
            world.RegisterPlayerColonySites(group.Key.UserId, group.Key.ColonyId, group.ToArray());
        Check(sites.All(site => world.Current!.PlayerColonySites.Any(found => WorldColonySites.Key(found) == WorldColonySites.Key(site))), "Registry and delivery use different site identity rules.");
    }

    private static void Wealth()
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-marker-wealth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new FileColonySnapshotIndexStore(root); var players = new PlayerRegistry(); var module = new PlayerSnapshotWealth(players, files);
            string source = Path.Combine(root, "wealth.rws");
            File.WriteAllText(source, "<savegame><meta><gameVersion>1.7</gameVersion></meta><game><maps><li><uniqueID>map</uniqueID><mapInfo><parent>parent</parent></mapInfo><wealthWatcher><wealthTotal>12345</wealthTotal></wealthWatcher><things /></li></maps></game></savegame>");
            var package = SaveSnapshotPackageBuilder.FromFile(source, new("owner", "colony", "wealth"), Now);
            var index = package.Index;
            files.StoreLatest(package, index, Now);
            var lightweight = files.GetLatest("owner", "colony")! with { Index = index with { Maps = [] } };
            Check(module.ForRaid("owner", "colony", lightweight, Now) == 12345, "Package wealth fallback failed.");
            players.RecordLatestSnapshotWealth("owner", "colony", "different", 1, Now);
            Check(module.ForRaid("owner", "colony", lightweight, Now) == 12345, "Different snapshot cache replaced wealth.");
            var foreign = lightweight with { Identity = new("owner", "colony", "old") };
            Check(module.ForRaid("owner", "colony", foreign, Now) == int.MaxValue, "Mismatched package supplied a different snapshot's wealth.");
            var summary = module.CacheLatest("owner", "colony", "reported", Now);
            Check(summary.CurrentSnapshotId == "wealth" && summary.LatestSnapshotWealth == 12345, "Player directory and raid wealth diverged.");
            Check(module.CacheLatest("missing", "colony", "reported", Now).LatestSnapshotWealth is null, "Missing snapshot invented wealth.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void RaidVisibility()
    {
        foreach (var status in Enum.GetValues<ServerEventStatus>())
        {
            var f = new Fixture(); f.Ledger.Append(RaidReadModuleTests.Raid("visibility") with { Status = status, TargetContext = new("world", "map", 1, EventLandingMode.MapEdge) });
            bool active = status is ServerEventStatus.Recorded or ServerEventStatus.ReadyForImmediateDelivery or ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.DeliveredToClient or ServerEventStatus.Conflict;
            Check(f.Raids.HasActor("attacker", "attack-colony") == active && f.Raids.HasParticipant("defender", "defend-colony") == active
                && !f.Raids.HasParticipant("defender", "other-colony") && (f.Raids.Build(Now).Count > 0) == active, "Raid marker/relocation status policy diverged: " + status);
        }
        var source = RaidReadModuleTests.Raid("excluded") with { TargetContext = new("world", "map", 1, EventLandingMode.MapEdge) };
        var payload = (RaidEventPayload)source.Payload;
        foreach (var excluded in new[] {
            payload with { AttackForce = null },
            payload with { AttackerLoss = ((RaidEventPayload)RaidReadModuleTests.Loss("loss").Payload).AttackerLoss },
            payload with { Settlement = new("original", "returned", 0, [], [], 0) },
            payload with { ReturnedSnapshotId = "returned" },
            payload with { OpponentKind = RaidOpponentKind.VanillaNpc } })
        {
            var f = new Fixture(); f.Ledger.Append(source with { Payload = excluded });
            Check(!f.Raids.HasActor("attacker", "attack-colony") && !f.Raids.HasParticipant("defender", "defend-colony")
                && f.Raids.Build(Now).Count == 0, "Ended, non-player or non-battle raid still blocked relocation or produced a destination.");
        }
    }

    private sealed class Fixture
    {
        internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
        internal readonly InMemoryColonySnapshotIndexStore Snapshots = new();
        internal readonly PlayerRegistry Players = new();
        internal readonly OnlinePresenceRegistry Presence = new();
        internal readonly DiplomacyRelationRegistry Relations = new();
        internal readonly WorldConfigurationRegistry World = new(WorldConfigurationExtensionService.Empty);
        internal readonly RuntimeWorldObjectMarkerRegistry Runtime = new();
        internal readonly RaidMarkerProjection Raids;
        internal readonly WorldMarkerDelivery Markers;
        internal ClashOfRimServerConfiguration Config = new();
        internal Fixture()
        {
            Raids = new(Ledger, Snapshots);
            Markers = new(Players, Presence, Relations, Snapshots, World, new(new ServerPluginRegistry(), new()), Runtime, Raids,
                new(Ledger, new(), new(), () => Config, NullLogger.Instance), new(Players, Snapshots), () => Config);
        }
        internal void Store(SaveSnapshotPackage package, string user, string colony, string id, SaveSnapshotIndex index)
        { var envelope = package.Envelope with { Identity = new(user, colony, id) }; Snapshots.StoreLatest(new(envelope.Identity, envelope, index, Now)); }
    }
    private static WorldObjectSummary Object(string id, string related)
        => new(id, id, "AIRsLight.ClashOfRim.RemoteSessionMapParent", "ClashOfRim_RemoteRaidBattleMapParent", "12", "Player", null, false) { ClashOfRimRelatedEventId = related };
    private static MapSummary Map(string id, string parent) => new(id, null, parent, "(10,1,10)", false, false, false, false, 0, 0);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
