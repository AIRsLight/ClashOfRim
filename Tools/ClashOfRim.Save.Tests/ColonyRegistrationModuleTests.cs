using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ColonyRegistrationModuleTests
{
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string mode in new[] { "success", "suppress", "extension-only", "empty", "null-site", "multi", "negative", "protocol", "identity", "token", "missing-token",
                    "wrong-user", "wrong-colony", "revoked", "replaced", "deleted", "unconfigured", "normalize-revoked", "normalize-policy", "normalize-world",
                    "prepare-revoked", "prepare-replaced", "prepare-failure", "replay-revoked", "replay-deleted", "replay-policy", "replay-world",
                    "occupied", "nearby", "move", "layer-move", "other-colony", "prepare-occupied", "prepare-nearby", "prepare-move", "persistence-failure" })
                { Register(mode); count++; }
            }
            Http(factory); count++;
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"Colony registration independent module scenarios passed: {count}");
    }

    private static void Http(Func<string, SaveSnapshotPackage> factory)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory);
        ColonyDeletionStageTests.SeedWorld(f.State.WorldConfiguration);
        f.Start(); f.Seed(); string before = f.Evidence();
        using var response = f.Post(ProtocolMessageKind.RegisterPlayerColonySites, new
        {
            protocolVersion = ProtocolApiVersion.Current, userId = "receiver", colonyId = "away", authToken = "invalid",
            sites = new[] { new PlayerColonySiteDto("receiver", "away", "intruder", "intruder", 0, "forged", tileLayerId: 2) }
        });
        var result = response.Content.ReadFromJsonAsync<RegisterPlayerColonySitesResponse>().GetAwaiter().GetResult()!;
        var failures = new List<string>();
        if (result.Result.Accepted || f.State.WorldConfiguration.Current!.PlayerColonySites.Any(x => x.UserId == "receiver"))
            failures.Add("Invalid token registered a colony site.");
        try { f.AssertPending(before, "Invalid site registration"); } catch (Exception e) { failures.Add(e.Message); }
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
        foreach (string mode in new[] { "missing-token", "wrong-user", "protocol", "empty", "null-site" })
        {
            using var invalid = f.Post(ProtocolMessageKind.RegisterPlayerColonySites,
                new RegisterPlayerColonySitesRequest(mode == "protocol" ? "invalid" : ProtocolApiVersion.Current, mode == "wrong-user" ? "intruder" : "receiver", "away",
                    mode == "empty" ? [] : mode == "null-site" ? [null!] : [Site()], authToken: mode == "missing-token" ? null : f.Token));
            Check(!(invalid.Content.ReadFromJsonAsync<RegisterPlayerColonySitesResponse>().GetAwaiter().GetResult()!).Result.Accepted, "Invalid HTTP registration accepted: " + mode);
            f.AssertPending(before, mode);
        }
        using var valid = f.Post(ProtocolMessageKind.RegisterPlayerColonySites,
            new RegisterPlayerColonySitesRequest(ProtocolApiVersion.Current, "receiver", "away", [Site()], authToken: f.Token));
        var accepted = valid.Content.ReadFromJsonAsync<RegisterPlayerColonySitesResponse>().GetAwaiter().GetResult()!;
        Check(accepted.Result.Accepted && accepted.AcceptedCount == 1 && accepted.WorldConfiguration!.PlayerColonySites.Any(x => x.UserId == "receiver" && x.TileLayerId == 2), "Valid HTTP registration failed.");
        f.AssertRecovered();
    }

    private static PlayerColonySiteDto Site(string user = "forged", string colony = "forged", int tile = 0, int layer = 2, string id = "object")
        => new(user, colony, id, "Map_23", tile, "新家", tileLayerId: layer);

    private static void Register(string mode)
    {
        var f = new Fixture();
        if (mode != "unconfigured") ColonyDeletionStageTests.SeedWorld(f.World);
        if (mode == "deleted") f.S.Players.MarkDeleted("user", "colony", null, DateTimeOffset.UtcNow);
        if (mode == "revoked") f.S.Tokens.RevokeForUser("user");
        if (mode == "replaced") { f.S.Sessions.EndUser("user"); f.S.Issue(); }
        void Occupy() => f.World.RegisterPlayerColonySites("enemy", "colony", [Site("enemy", "colony")]);
        void Move() => f.World.RegisterPlayerColonySites("user", "colony", [Site("user", "colony", tile: 8)]);
        if (mode == "occupied") Occupy();
        if (mode == "nearby") f.Distances.Result = 1;
        if (mode == "move") Move();
        if (mode == "layer-move") f.World.RegisterPlayerColonySites("user", "colony", [Site("user", "colony", layer: 3)]);
        if (mode == "other-colony") f.World.RegisterPlayerColonySites("user", "other", [Site("user", "other")]);
        string before = f.Evidence(); int playerWrites = f.S.PlayerSlot.Writes;
        void Change(string phase)
        {
            if (mode == phase + "-revoked") f.S.Tokens.RevokeForUser("user");
            if (mode == phase + "-replaced") { f.S.Sessions.EndUser("user"); f.S.Issue(); }
            if (mode == phase + "-policy") f.Baseline.ReplaceBaseline(EntryTestSupport.Manifest("changed"), "admin", DateTimeOffset.UtcNow);
            if (mode == phase + "-world") f.World.Submit("admin", f.World.Current!); // A new baseline clears its substrate until uploaded again.
            if (mode == phase + "-deleted") f.S.Players.MarkDeleted("user", "colony", null, DateTimeOffset.UtcNow);
            if (mode == phase + "-occupied") Occupy();
            if (mode == phase + "-nearby") f.Distances.Result = 1;
            if (mode == phase + "-move") Move();
            before = f.Evidence(); playerWrites = f.S.PlayerSlot.Writes;
        }
        f.Provider.BeforeNormalize = () => { Check(!Monitor.IsEntered(f.S.Gate), "Extensions normalized under server gate."); Change("normalize"); };
        f.S.Recovery.NeedsPreparation = true;
        f.S.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate), "Recovery prepared under server gate."); Change("prepare");
            if (mode == "prepare-failure") throw new IOException("Injected preparation failure.");
        };
        f.S.Recovery.OnReplay = () => Change("replay");
        if (mode is "success" or "suppress" or "extension-only" or "persistence-failure")
            f.Slot.Before = _ => Check(Monitor.IsEntered(f.S.Gate), "World registration published outside its mutation boundary.");
        f.Slot.Fail = mode == "persistence-failure";
        var request = new RegisterPlayerColonySitesRequest(mode == "protocol" ? "bad" : ProtocolApiVersion.Current,
            mode == "identity" ? " " : mode == "wrong-user" ? "other" : "user", mode == "wrong-colony" ? "other" : "colony",
            mode is "empty" or "extension-only" ? [] : mode == "negative" ? [Site(tile: -1)] : mode == "null-site" ? [null!] : mode == "multi" ? [Site(), Site(tile: 7, id: "other")] : [Site(), Site()],
            mode == "extension-only" ? [new("test.registration", "kind", "1", "payload")] : [],
            suppressWorldConfigurationNotification: mode == "suppress", authToken: mode == "token" ? "bad" : mode == "missing-token" ? null : f.Token);
        RegisterPlayerColonySitesResponse? result = null; bool failed = false;
        try { result = f.Module.Register(request); } catch (IOException) { failed = true; }
        Check(failed == (mode is "prepare-failure" or "persistence-failure"), "Unexpected registration exception: " + mode);
        bool accepted = mode is "success" or "suppress" or "extension-only";
        if (!failed) Check(result!.Result.Accepted == accepted, "Unexpected registration acceptance: " + mode);
        if (accepted)
        {
            var stored = f.Reopen().Current!;
            Check(result!.AcceptedCount == (mode == "extension-only" ? 0 : 1) && result.WorldMapMarkers is not null
                && f.S.Players.FindByUserId("user")?.ColonyId == "colony", "Registration result/activity missing.");
            Check(f.Notifications.GetVersion("user") == (mode == "suppress" ? 0 : 1) && f.Notifications.GetVersion("watcher") == (mode == "suppress" ? 0 : 1), "Notification recipients/suppression changed.");
            if (mode == "extension-only") Check(stored.Extensions.Single().PayloadJson == "payload" && stored.PlayerColonySites.All(x => x.UserId != "user"), "Extension-only sync created a site.");
            else Check(stored.PlayerColonySites.Single(x => x.UserId == "user") is { ColonyId: "colony", Tile: 0, TileLayerId: 2, Label: "新家" }, "Payload supplied another owner or lost coordinates.");
        }
        else
        {
            Check(f.Evidence() == before && f.S.PlayerSlot.Writes == playerWrites && !Monitor.IsEntered(f.S.Gate), "Rejected/failed registration changed world/activity or leaked gate: " + mode);
            if (mode is "protocol" or "identity" or "token" or "missing-token" or "wrong-user" or "wrong-colony" or "revoked" or "replaced" or "deleted" or "unconfigured" or "empty" or "null-site" or "multi" or "negative")
                Check(f.S.Recovery.Replays == 0, "Invalid request recovered unrelated business: " + mode);
        }
        if (mode is "prepare-failure" or "persistence-failure")
        {
            f.Slot.Fail = false; f.S.Recovery.OnPrepare = null;
            Check(f.Module.Register(request).Result.Accepted && f.Reopen().Current!.PlayerColonySites.Any(x => x.UserId == "user") && f.Notifications.GetVersion("user") == 1, "Failed registration did not retry cleanly.");
        }
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly RecoveryTestSupport.Slot Slot = new();
        internal readonly WorldConfigurationPersistenceTests.MemoryBinary Binary = new();
        internal readonly CompatibilityBaselineRegistry Baseline = new();
        internal readonly EventNotificationHub Notifications = new();
        internal readonly WorldConfigurationAuthorizationTests.Provider Provider = new() { Keys = [new("test.registration", "kind")] };
        internal readonly Distances Distances = new();
        internal readonly ServerPluginRegistry Plugins;
        internal readonly WorldConfigurationRegistry World;
        internal readonly ColonySiteRegistration Module;
        internal readonly string Token;
        internal Fixture()
        {
            Plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.registration", "test", "1", "test", "", [], WorldConfigurationExtensionProviders: [Provider])]);
            World = Reopen(); Token = S.Issue().Token; S.Players.Record("watcher", "other", null, DateTimeOffset.UtcNow);
            var relations = new DiplomacyRelationRegistry(); var delivery = new WorldConfigurationDelivery(Plugins, Baseline);
            var protection = new AIRsLight.ClashOfRim.Network.Modules.Raids.RaidProtection(S.Ledger, new(), new(), () => S.Configuration, NullLogger.Instance);
            var markers = new WorldMarkerDelivery(S.Players, S.Presence, relations, S.Snapshots, World, delivery, new(),
                new(S.Ledger, S.Snapshots), protection, new(S.Players, S.Snapshots), () => S.Configuration);
            Module = new(new(S.Tokens, S.Sessions, relations, new(), S.Ledger, () => S.Configuration.PvpEnabled), S.Players, S.Mutations,
                World, Baseline, Plugins, Distances, S.Activity, delivery, markers, new(S.Players, Notifications), () => NullLogger.Instance);
        }
        internal WorldConfigurationRegistry Reopen() => new(Slot, Binary, Plugins.ActiveWorldConfigurationExtensions(Baseline.Current));
        internal string Evidence() => JsonSerializer.Serialize(new { World = World.Current, Persisted = Reopen().Current, Owner = Notifications.GetVersion("user"), Watcher = Notifications.GetVersion("watcher") });
    }
    private sealed class Distances : IWorldTileDistanceCalculator
    {
        internal int? Result;
        public int? TryCalculateDistance(WorldTileGeometryDistanceSource? geometry, WorldTileRef fromTile, WorldTileRef toTile, int crossLayerOverheadDistanceTiles) => Result;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Colony registration: " + message); }
}
