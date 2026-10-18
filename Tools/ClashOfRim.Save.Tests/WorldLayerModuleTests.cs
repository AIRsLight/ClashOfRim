using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorldLayerModuleTests
{
    private const string Layer = BiotechCompatibilityKeys.WorldPollution;
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(3);
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var package = factory("world-layers"); var original = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (string mode in new[] { "raw", "gzip", "deflate", "inactive", "unsupported", "corrupt-gzip", "bad-xml", "empty", "missing", "stale", "no-accepted", "fallback", "unconfigured", "index-only", "world-failure" })
                { Projection(package, mode); count++; }
                foreach (string mode in new[] { "near", "edge", "far", "unknown-distance", "online", "other-generation", "duplicate", "surface", "fallback-layer", "no-geometry", "unknown-layer" })
                { Notification(mode); count++; }
                foreach (string mode in new[] { "ordered", "inactive", "unconfigured", "invalid-owner", "invalid-colony", "persistence" })
                { Extensions(package, mode); count++; }
                RecoverWithoutSnapshot(package); count++;
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
        Console.WriteLine($"World layer independent module scenarios passed: {count}");
    }

    private static byte[] Payload(bool deflate = false)
    {
        byte[] pollution = [0, 128, 0, 192];
        if (deflate)
        {
            using var output = new MemoryStream();
            using (var stream = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true)) stream.Write(pollution);
            pollution = output.ToArray();
        }
        string field = deflate ? "tilePollutionDeflate" : "tilePollution";
        return Encoding.UTF8.GetBytes($"<savegame><game><world><grid><layers><values><li><def>Surface</def><{field}>{Convert.ToBase64String(pollution)}</{field}></li></values></layers></grid></world></game></savegame>");
    }

    private static void Projection(SaveSnapshotPackage source, string mode)
    {
        using var f = new Fixture();
        byte[] bytes = mode == "bad-xml" ? Encoding.UTF8.GetBytes("<bad") : Payload(mode == "deflate");
        var encoding = SnapshotPayloadEncoding.RawRws;
        if (mode == "gzip")
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(bytes);
            bytes = output.ToArray(); encoding = SnapshotPayloadEncoding.GzipRws;
        }
        if (mode == "corrupt-gzip") { bytes = [1, 2, 3, 4]; encoding = SnapshotPayloadEncoding.GzipRws; }
        if (mode == "empty") bytes = [];
        if (mode == "unsupported") encoding = (SnapshotPayloadEncoding)999;
        var identity = new SnapshotIdentity("actor", "home", "snapshot");
        var package = source with { Envelope = source.Envelope with { Identity = identity, PayloadEncoding = encoding }, Payload = bytes };
        f.Files.StoreLatest(package, package.Index, Now);
        LatestSnapshotRecord? accepted = f.Files.GetLatest("actor", "home")!;
        if (mode == "missing") f.Files.RemoveLatest("actor", "home");
        if (mode == "stale") accepted = accepted with { Identity = identity with { SnapshotId = "old" } };
        if (mode == "no-accepted") accepted = null;
        if (mode == "fallback") accepted = accepted! with { Identity = new(null, null, "snapshot") };
        if (mode == "inactive") f.Baseline.ReplaceBaseline(EntryTestSupport.Manifest(), "admin", Now);
        if (mode == "unconfigured") f.World.Submit("admin", f.World.Current!);
        var module = f.Projection(mode == "index-only" ? new InMemoryColonySnapshotIndexStore() : f.Files);
        string before = f.Evidence(); f.Slot.Fail = mode == "world-failure";
        f.Slot.Before = _ => Check(Monitor.IsEntered(f.Gate), "Layer publication escaped mutation gate.");
        bool failed = false;
        try { module.Project("actor", "home", accepted, Now); } catch (IOException) { failed = true; }
        Check(failed == (mode == "world-failure"), "Unexpected projection persistence outcome: " + mode);
        bool changed = mode is "raw" or "gzip" or "deflate" or "fallback";
        if (!changed) Check(before == f.Evidence(), "Skipped/failed projection mutated world: " + mode);
        if (failed) { f.Slot.Fail = false; module.Project("actor", "home", accepted, Now); changed = true; }
        if (changed)
        {
            var values = f.Extensions.ReadTileFloatLayer(f.Reopen().Current!.Extensions, Layer);
            Check(values.Count == 2 && values.Single(x => x.Tile == 0).Value > 0.49f && values.Single(x => x.Tile == 1).Value > 0.74f
                && f.Ledger.ListAll().Count == 6 && !f.World.HasPendingLayerNotifications, "Projection lost zero tile, increases, notifications or acknowledgement.");
            Check(f.Notices.GetVersion("owner") > 0 && f.WorldNotices.GetVersion("observer") > 0, "Recipients were not signaled.");
            string done = f.Evidence(); module.Project("actor", "home", accepted, Now); Check(done == f.Evidence(), "Completed projection repeated effects.");
            f.Baseline.ReplaceBaseline(EntryTestSupport.Manifest(), "admin", Now);
            module.Project("actor", "home", accepted, Now); Check(done == f.Evidence(), "Disabled plugin changed world state.");
        }
        Check(!Monitor.IsEntered(f.Gate) && f.Recovery.Replays == 0, "Projection recovered unrelated snapshot business or leaked gate.");
    }

    private static void Notification(string mode)
    {
        using var f = new Fixture();
        f.Players.Record("actor", mode == "other-generation" ? "rebuilt" : "home", null, Now, "玩家甲");
        var configuration = f.World.Current!;
        var node = JsonSerializer.SerializeToNode(configuration)!;
        // Include inputs the persisted registry normally filters, to constrain notification recipient policy itself.
        node["PlayerColonySites"]!.AsArray().Add(JsonSerializer.SerializeToNode(new PlayerColonySiteDto("actor", "other", "own", null, 0, "own")));
        node["PlayerColonySites"]!.AsArray().Add(JsonSerializer.SerializeToNode(new PlayerColonySiteDto("", "bad", null, null, 0, "bad")));
        node["PlayerColonySites"]!.AsArray().Add(JsonSerializer.SerializeToNode(new PlayerColonySiteDto("negative", "bad", null, null, -1, "bad")));
        int expectedLayer = 0;
        if (mode is "surface" or "fallback-layer")
        {
            node["TileGeometry"] = JsonSerializer.SerializeToNode(new WorldTileGeometryDto([
                new(8, mode == "surface" ? "sUrFaCe" : "Orbit", 1, [new(0, 1, 0, 0)]), new(2, "Underground", 1, [new(0, 1, 0, 0)])]));
            expectedLayer = mode == "surface" ? 8 : 2;
        }
        if (mode == "no-geometry") node["TileGeometry"] = null;
        configuration = node.Deserialize<WorldConfigurationDto>()!;
        f.Distance.Value = mode == "edge" ? 4 : mode == "far" ? 5 : mode == "unknown-distance" ? null : 0;
        OnlinePresenceLease? online = null;
        if (mode == "online") Check(f.Presence.TryConnectExclusive("owner", out online), "Cannot connect fixture.");
        using (online)
        {
            var increase = new WorldTileFloatLayerIncrease(mode == "unknown-layer" ? "unknown" : Layer, 0, 0.1f, 0.5f, 0.4f);
            var events = f.Notifications.Build(configuration, f.Extensions, new("actor", "home", "snapshot"), mode == "duplicate" ? [increase, increase] : [increase], Now);
            bool expected = mode is not ("far" or "unknown-distance" or "unknown-layer");
            Check(events.Count == (expected ? 3 : 0), "Wrong recipient filtering/radius/dedup: " + mode);
            foreach (var item in events)
            {
                var payload = (ServerNotificationEventPayload)item.Payload;
                Check(item.CreatedAtUtc == Now && item.Actor == new EventParty("actor", "home", "snapshot")
                    && item.TargetContext!.LandingMode == EventLandingMode.MapEdge
                    && payload.Message.Contains(mode == "other-generation" ? "actor" : "玩家甲", StringComparison.Ordinal), "Notification identity/label changed.");
                Check(item.DeliveryMode == (mode == "online" && item.Target.UserId == "owner" ? ServerEventDeliveryMode.OnlineImmediate : ServerEventDeliveryMode.OfflinePending), "Wrong online delivery mode.");
            }
            Check(f.Distance.Calls.All(x => x.From.LayerId == expectedLayer && x.To.LayerId == expectedLayer && x.Overhead == 0), "Surface distance/fallback semantics changed.");
            Check(f.Ledger.ListAll().Count == 0 && !f.World.HasPendingLayerNotifications, "Notification building published effects.");
        }
    }

    private static void RecoverWithoutSnapshot(SaveSnapshotPackage source)
    {
        using var f = new Fixture();
        var identity = new SnapshotIdentity("actor", "home", "snapshot");
        var package = source with { Envelope = source.Envelope with { Identity = identity, PayloadEncoding = SnapshotPayloadEncoding.RawRws }, Payload = Payload() };
        f.Files.StoreLatest(package, package.Index, Now);
        var module = f.Projection(f.Files);
        f.Slot.Before = json =>
        {
            if (JsonNode.Parse(json)!["PendingLayerNotifications"]!.AsArray().Count == 0)
                throw new IOException("Injected acknowledgement failure.");
        };
        bool failed = false;
        try { module.Project("actor", "home", f.Files.GetLatest("actor", "home"), Now); } catch (IOException) { failed = true; }
        Check(failed && f.World.HasPendingLayerNotifications && f.Ledger.ListAll().Count == 6, "Acknowledgement fault did not preserve durable work.");
        f.Slot.Before = null;
        f.Players.Record("late-player", "rebuilt", null, Now.AddDays(1));
        f.Files.RemoveLatest("actor", "home");
        module.Project("actor", "home", null, Now.AddDays(1));
        Check(!f.World.HasPendingLayerNotifications && f.Ledger.ListAll().Count == 6 && f.WorldNotices.GetVersion("late-player") == 1,
            "Missing snapshot prevented pending recovery or omitted newly registered players.");
    }

    private static void Extensions(SaveSnapshotPackage package, string mode)
    {
        using var f = new Fixture(); var calls = new List<string>();
        var provider = new WorldConfigurationAuthorizationTests.Provider { Keys = [new("test.rebuild", "kind")] };
        provider.FromSnapshot = context =>
        {
            string key = context.Snapshot.AcceptedAtUtc.ToUnixTimeSeconds().ToString("D12", CultureInfo.InvariantCulture) + ":" + context.UserId + ":" + context.ColonyId;
            calls.Add(context.UserId + "/" + context.ColonyId);
            string? current = context.CurrentExtensions?.FirstOrDefault(x => x.ProviderId == "test.rebuild")?.PayloadJson;
            return [new("test.rebuild", "kind", "1", string.CompareOrdinal(current, key) > 0 ? current! : key)];
        };
        var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.rebuild", "test", "1", "test", "", [], WorldConfigurationExtensionProviders: [provider], RequiredPackageIds: ["test.rebuild"])]);
        var baseline = new CompatibilityBaselineRegistry();
        if (mode != "inactive") baseline.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [new ModManifestEntry { PackageId = "test.rebuild" }] }, "admin", Now);
        var projection = new SnapshotColonyProjection(f.World, plugins, baseline, new(plugins, baseline), new(f.Players, f.WorldNotices));
        LatestSnapshotRecord Snapshot(string user, string colony, int day)
        {
            var identity = new SnapshotIdentity(user, colony, user + colony);
            return new(identity, package.Envelope with { Identity = identity }, package.Index, Now.AddDays(day));
        }
        var snapshots = new[] { Snapshot("C", "c", 2), Snapshot("B", "b", 1), Snapshot("A", "z", 1), Snapshot("A", "a", 1) };
        if (mode == "invalid-owner") snapshots = [Snapshot("", "c", 1)];
        if (mode == "invalid-colony") snapshots = [Snapshot("A", " ", 1)];
        if (mode == "unconfigured") f.World.Submit("admin", f.World.Current!);
        string sites = JsonSerializer.Serialize(f.World.Current?.PlayerColonySites); string before = f.Evidence();
        f.Slot.Fail = mode == "persistence"; bool failed = false; int recovered = -1;
        try { recovered = projection.RecoverExtensions(snapshots); } catch (IOException) { failed = true; }
        Check(failed == (mode == "persistence"), "Wrong extension persistence failure.");
        if (mode is "unconfigured" or "invalid-owner" or "invalid-colony") Check(recovered == 0 && calls.Count == 0 && before == f.Evidence(), "Invalid rebuild changed extensions: " + mode);
        if (mode == "inactive") Check(recovered == 1 && calls.Count == 0 && f.World.Current!.Extensions.Count == 0
            && JsonSerializer.Serialize(f.World.Current.PlayerColonySites) == sites && projection.RecoverExtensions(snapshots) == 0,
            "Inactive extension cleanup or repeat semantics changed.");
        if (failed) { Check(before == f.Evidence(), "Failed rebuild published candidate cache."); f.Slot.Fail = false; calls.Clear(); recovered = projection.RecoverExtensions(snapshots); }
        if (mode is "ordered" or "persistence")
        {
            Check(recovered == 4 && calls.SequenceEqual(new[] { "A/a", "A/z", "B/b", "C/c" }), "Startup extension ordering changed.");
            Check(JsonSerializer.Serialize(f.World.Current!.PlayerColonySites) == sites && f.WorldNotices.GetVersion("observer") == 0,
                "Startup recovery registered sites or emitted live notices.");
            int writes = f.Slot.Writes;
            Check(projection.RecoverExtensions(snapshots) == 0 && f.Slot.Writes == writes, "Unchanged extension recovery rewrote world.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cor-layer-module-" + Guid.NewGuid().ToString("N"));
        internal readonly object Gate = new();
        internal readonly EconomicModuleTestContext.RecoveryProbe Recovery = new();
        internal readonly RecoveryTestSupport.Slot Slot = new();
        internal readonly WorldConfigurationPersistenceTests.MemoryBinary Binary = new();
        internal readonly PlayerRegistry Players = new();
        internal readonly OnlinePresenceRegistry Presence = new();
        internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
        internal readonly EventNotificationHub Notices = new(), WorldNotices = new();
        internal readonly CompatibilityBaselineRegistry Baseline = new();
        internal readonly ServerPluginRegistry Plugins = new ServerPluginRegistry().WithBuiltInPlugins([BiotechServerCompatibility.Descriptor]);
        internal readonly WorldConfigurationExtensionService Extensions = new([new BiotechWorldPollutionExtensionProvider()]);
        internal readonly Distance Distance = new();
        internal readonly FileColonySnapshotIndexStore Files;
        internal readonly WorldConfigurationRegistry World;
        internal readonly SnapshotMutationCoordinator Mutations;
        internal readonly WorldLayerNotifications Notifications;
        internal Fixture()
        {
            Files = new(root); World = Reopen(); Mutations = new(Gate, Recovery);
            ColonyDeletionStageTests.SeedWorld(World); World.ConfirmWorldTileFloatLayer(Layer, [new(1, 0.2f)], Extensions);
            Players.Record("observer", "home", null, Now);
            Baseline.ReplaceBaseline(EntryTestSupport.Manifest() with { Mods = [new ModManifestEntry { PackageId = BiotechCompatibilityKeys.PackageId }] }, "admin", Now);
            Notifications = new(World, Ledger, Players, Presence, Distance, Mutations, Notices, new(Players, WorldNotices));
        }
        internal WorldConfigurationRegistry Reopen() => new(Slot, Binary, Extensions);
        internal SnapshotWorldLayers Projection(IColonySnapshotIndexStore store) => new(store, World, Plugins, Baseline, Players, Mutations, Notifications, () => NullLogger.Instance);
        internal string Evidence() => JsonSerializer.Serialize(new { World = World.Current, Disk = Slot.Read(), Events = Ledger.ListAll(), Notice = Notices.GetVersion("owner"), WorldNotice = WorldNotices.GetVersion("observer") });
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    private sealed class Distance : IWorldTileDistanceCalculator
    {
        internal int? Value = 0;
        internal readonly List<(WorldTileRef From, WorldTileRef To, int Overhead)> Calls = [];
        public int? TryCalculateDistance(WorldTileGeometryDistanceSource? geometry, WorldTileRef fromTile, WorldTileRef toTile, int crossLayerOverheadDistanceTiles)
        { Calls.Add((fromTile, toTile, crossLayerOverheadDistanceTiles)); return Value; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("World layer module: " + message); }
}
