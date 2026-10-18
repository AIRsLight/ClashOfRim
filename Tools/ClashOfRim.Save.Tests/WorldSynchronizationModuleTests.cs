using System.Collections;
using System.Globalization;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorldSynchronizationModuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        int count = 0; void Case(Action action) { action(); count++; }
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string kind in new[] { "owner", "owner-delimiter", "colony", "object", "escaped", "unicode" }) Case(() => Identity(kind));
                foreach (string kind in new[] { "enumeration", "path", "duplicate", "filter", "expiry", "delayed-publication", "clear" }) Case(() => Batch(kind));
                foreach (string kind in new[] { "success", "identity", "token", "wrong-user", "wrong-colony", "revoked", "replaced", "prepare-revoked", "prepare-failure",
                    "recovery-replaced", "replay-revoked", "recovery-failure" }) Case(() => Replace(kind));
                foreach (string kind in new[] { "empty", "current", "failure" }) Case(() => Read(kind));
            }
            Case(() => HttpBoundaries(factory, runtime: true)); Case(() => HttpBoundaries(factory, runtime: false));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"World synchronization scenarios passed: {count}");
    }

    private static void Identity(string kind)
    {
        var registry = new RuntimeWorldObjectMarkerRegistry();
        var first = (User: "a_b", Colony: "colony", Object: "object");
        var second = (User: "a-b", Colony: "colony", Object: "object");
        if (kind == "owner-delimiter") { first = ("a\nb", "c", "object"); second = ("a", "b\nc", "object"); }
        if (kind == "colony") { first = ("user", "c_d", "object"); second = ("user", "c-d", "object"); }
        if (kind == "object") { first = ("user", "colony", "obj_a"); second = ("user", "colony", "obj-a"); }
        if (kind == "escaped") { first = ("a:b", "c%", "object:"); second = ("a%3Ab", "c%", "object:"); }
        if (kind == "unicode") { first = ("I世界", "c", "object"); second = ("ı世界", "c", "object"); }
        registry.ReplaceForOwner(first.User, first.Colony, "one", [Marker(first.Object)], Now);
        if (kind == "object") registry.ReplaceForOwner(first.User, first.Colony, "two", [Marker(first.Object), Marker(second.Object)], Now);
        else registry.ReplaceForOwner(second.User, second.Colony, "two", [Marker(second.Object)], Now);
        var both = registry.ListVisibleMarkers("viewer", Now);
        Check(both.Count == 2 && both.Select(m => m.MarkerId).Distinct(StringComparer.Ordinal).Count() == 2, "Identity collision: " + kind);
        int removed = registry.RemoveForOwner(first.User, first.Colony);
        var remaining = registry.ListVisibleMarkers("viewer", Now);
        Check(removed == (kind == "object" ? 2 : 1) && remaining.Count == (kind == "object" ? 0 : 1)
            && remaining.All(m => m.OwnerUserId == second.User && m.ColonyId == second.Colony), "Cleanup crossed owner identity: " + kind);
        Check(registry.RemoveForOwner(first.User, first.Colony) == 0, "Repeated removal was not a no-op.");
    }

    private static void Batch(string kind)
    {
        var registry = new RuntimeWorldObjectMarkerRegistry();
        registry.ReplaceForOwner("user", "colony", "old", [Marker("old")], Now);
        registry.ReplaceForOwner("other", "colony", "other", [Marker("other")], Now);
        Check(registry.ListVisibleMarkers("user", Now).Single().OwnerUserId == "other", "Own objects were echoed.");
        if (kind == "enumeration")
        {
            IEnumerable<RuntimeWorldObjectMarkerDto> Broken() { yield return Marker("new"); throw new IOException("Injected enumeration failure."); }
            bool failed = false; try { registry.ReplaceForOwner("user", "colony", "new", Broken(), Now); } catch (IOException) { failed = true; }
            Check(failed && registry.ListVisibleMarkers("viewer", Now).Single(m => m.OwnerUserId == "user").WorldObjectId == "old", "Failed enumeration partially published a batch.");
        }
        if (kind == "path")
        {
            int[] path = [0, 2, 4]; registry.ReplaceForOwner("user", "colony", "new", [Marker("new", path)], Now); path[0] = 99;
            var read = registry.ListVisibleMarkers("other", Now).Single();
            Check(read.PathTiles!.SequenceEqual(new[] { 0, 2, 4 }), "Caller mutated a published path.");
            bool readOnly = false; try { ((IList<int>)read.PathTiles!)[1] = 99; } catch (NotSupportedException) { readOnly = true; }
            Check(readOnly && registry.ListVisibleMarkers("other", Now).Single().PathTiles!.SequenceEqual(new[] { 0, 2, 4 }), "Reader mutated stored path data.");
        }
        if (kind == "duplicate")
        {
            Check(registry.ReplaceForOwner("user", "colony", "new", [Marker("same"), new("same", "Shuttle", "shuttle", 3, "last", [1, 3], 2)], Now) == 2,
                "Accepted input count changed for duplicate IDs.");
            var marker = registry.ListVisibleMarkers("other", Now).Single();
            Check(marker.Label == "last" && marker.Kind == WorldMapMarkerKind.RuntimeShuttle && marker.Tile == 3 && marker.TileLayerId == 2
                && marker.SnapshotId == "new" && !marker.TradeEnabled && !marker.ReinforcementEnabled, "Last duplicate or runtime capabilities changed.");
        }
        if (kind == "filter")
        {
            Check(registry.ReplaceForOwner("user", "colony", null,
                [null!, Marker(""), new("invalid", "x", "x", -1, "invalid"), Marker("zero"), new("pod", "pod", "TRANSPORTPOD", 2, "pod"), new("unknown", null, "mod-object", 3, "unknown")], Now) == 3,
                "Invalid markers were accepted or valid markers lost.");
            var markers = registry.ListVisibleMarkers("other", Now);
            Check(markers.Single(m => m.WorldObjectId == "zero").Tile == 0 && markers.Any(m => m.Kind == WorldMapMarkerKind.RuntimeTransportPod)
                && markers.Any(m => m.Kind == WorldMapMarkerKind.RuntimeWorldObject), "Zero tile or kind fallback changed.");
        }
        if (kind == "clear")
        {
            Check(registry.ReplaceForOwner("user", "colony", null, [], Now) == 0 && registry.ListVisibleMarkers("viewer", Now).Single().OwnerUserId == "other",
                "Empty batch failed to clear its owner or cleared another owner.");
        }
        if (kind == "delayed-publication")
        {
            var prepared = registry.Prepare("user", "colony", "delayed", [Marker("new")]);
            registry.Publish(prepared, Now.AddSeconds(120));
            var marker = registry.ListVisibleMarkers("viewer", Now.AddSeconds(209)).Single();
            Check(marker.SnapshotId == "delayed" && marker.CreatedAtUtc == Now.AddSeconds(120)
                && registry.ListVisibleMarkers("viewer", Now.AddSeconds(210)).Count == 0, "Slow preparation consumed the published lease or left stale timestamps.");
        }
        if (kind == "expiry")
        {
            Check(registry.ListVisibleMarkers("viewer", Now.AddSeconds(89)).Count == 2, "Lease expired early.");
            registry.ReplaceForOwner("user", "colony", "renewed", [Marker("new")], Now.AddSeconds(40));
            var renewed = registry.ListVisibleMarkers("viewer", Now.AddSeconds(90)).Single();
            Check(renewed.OwnerUserId == "user" && renewed.SnapshotId == "renewed", "Old lease removed the renewed batch or retained the expired owner.");
            Check(registry.ListVisibleMarkers("viewer", Now.AddSeconds(130)).Count == 0 && registry.RemoveForOwner("user", "colony") == 0, "Lease boundary retained stale indexes.");
        }
    }

    private static void Replace(string kind)
    {
        var f = new Fixture(); var token = f.S.Issue().Token; f.Seed(); string before = f.Evidence();
        if (kind == "revoked") f.S.Tokens.RevokeForUser("user");
        if (kind == "replaced") f.S.Issue();
        int enumerations = 0;
        var incoming = new ObservedList([Marker("new", [0, 1, 2])], () =>
        {
            enumerations++; Check(!Monitor.IsEntered(f.S.Gate), "Runtime batch prepared under the global mutation gate.");
            if (kind == "prepare-revoked") f.S.Tokens.RevokeForUser("user");
            if (kind == "prepare-failure") throw new IOException("Injected enumeration failure.");
        });
        f.S.Recovery.NeedsPreparation = true;
        f.S.Recovery.OnPrepare = () =>
        {
            Check(!Monitor.IsEntered(f.S.Gate) && f.Evidence() == before, "Recovery prepared under the gate or after marker publication.");
            if (kind == "recovery-replaced") f.S.Issue();
            if (kind == "recovery-failure") throw new IOException("Injected recovery failure.");
        };
        f.S.Recovery.OnReplay = () => { if (kind == "replay-revoked") f.S.Tokens.RevokeForUser("user"); };
        SyncRuntimeWorldObjectsResponse? response = null; bool threw = false;
        try { response = f.Sync.Replace(new(kind == "identity" ? "" : kind == "wrong-user" ? "other" : "user", kind == "wrong-colony" ? "other" : "colony",
            "reported", Now.AddYears(1), incoming, kind == "token" ? "invalid" : token)); } catch (IOException) { threw = true; }
        Check(threw == (kind is "prepare-failure" or "recovery-failure"), "Unexpected runtime failure: " + kind);
        if (!threw) Check(response!.Result.Accepted == (kind == "success"), "Runtime publication bypassed authorization: " + kind);
        if (kind == "success")
        {
            var marker = f.Runtime.ListVisibleMarkers("other", DateTimeOffset.UtcNow).Single();
            Check(response!.AcceptedCount == 1 && marker.WorldObjectId == "new" && marker.SnapshotId == "reported"
                && marker.CreatedAtUtc == response.AcceptedAtUtc && response.AcceptedAtUtc < Now.AddMinutes(1)
                && response.WorldMapMarkers!.Markers.All(m => m.OwnerUserId != "user"), "Runtime response lost ownership or trusted client time.");
        }
        else Check(f.Evidence() == before && (response is null || response.AcceptedCount == 0 && response.WorldMapMarkers is null), "Denied/failed runtime sync changed the old batch.");
        if (kind is "identity" or "token" or "wrong-user" or "wrong-colony" or "revoked" or "replaced")
            Check(enumerations == 0 && f.S.Recovery.Replays == 0, "Unauthorized runtime sync prepared data or recovered business.");
        Check(!Monitor.IsEntered(f.S.Gate), "Runtime sync leaked the mutation gate.");
        f.S.Recovery.OnPrepare = f.S.Recovery.OnReplay = null;
        Check(f.Sync.Replace(new("user", "colony", "retry", Now, [Marker("retry")], f.S.Issue().Token)).Result.Accepted, "Valid runtime retry did not recover.");
    }

    private static void Read(string kind)
    {
        var f = new Fixture(); f.Seed(); f.S.Configuration = new(giftsEnabled: false, pvpEnabled: false);
        f.S.Recovery.NeedsPreparation = true; int preparations = 0;
        f.S.Recovery.OnPrepare = () =>
        {
            preparations++; Check(!Monitor.IsEntered(f.S.Gate), "Marker read nested detached recovery under the mutation gate.");
            if (kind == "failure") throw new IOException("Injected recovery failure.");
        };
        if (kind == "failure")
        {
            bool failed = false; try { f.Sync.Read(new("user", Now, "colony")); } catch (IOException) { failed = true; }
            Check(failed && !Monitor.IsEntered(f.S.Gate), "Marker read swallowed recovery failure or leaked the gate."); return;
        }
        var result = f.Sync.Read(new(kind == "empty" ? " " : "user", Now.AddYears(1), "colony"));
        if (kind == "empty") Check(result.UserId == "" && result.Markers.Count == 0 && preparations == 0 && f.S.Recovery.Replays == 0, "Empty marker read recovered unrelated business.");
        else Check(result.UserId == "user" && result.Markers.Single().OwnerUserId == "other" && !result.GiftsEnabled && !result.PvpEnabled && preparations == 1,
            "Marker read lost current flags/visibility or treated KnownAtUtc as an authoritative version.");
    }

    private static void HttpBoundaries(Func<string, SaveSnapshotPackage> factory, bool runtime)
    {
        using var f = new HttpSnapshotRecoveryTests.Fixture(factory); f.Start(); f.Seed(); string before = f.Evidence();
        var kind = runtime ? ProtocolMessageKind.SyncRuntimeWorldObjects : ProtocolMessageKind.SyncWorldMapMarkers;
        using (var response = f.Post(kind, runtime ? (object)new SyncRuntimeWorldObjectsRequest("receiver", "away", null, Now, [], "invalid") : new SyncWorldMapMarkersRequest("", Now)))
        { response.EnsureSuccessStatusCode(); f.AssertPending(before, "Rejected/empty world sync"); }
        using var valid = f.Post(kind, runtime ? (object)new SyncRuntimeWorldObjectsRequest("receiver", "away", "reported", Now, [Marker("http")], f.Token) : new SyncWorldMapMarkersRequest("receiver", Now, "away"));
        valid.EnsureSuccessStatusCode(); f.AssertRecovered();
        if (runtime)
        {
            var body = JsonSerializer.Deserialize<SyncRuntimeWorldObjectsResponse>(valid.Content.ReadAsStringAsync().GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Check(body.Result.Accepted && body.AcceptedCount == 1 && f.State.RuntimeWorldObjectMarkers.ListVisibleMarkers("viewer", DateTimeOffset.UtcNow).Single().WorldObjectId == "http",
                "HTTP runtime sync changed DTO binding or omitted marker publication.");
        }
    }

    private sealed class Fixture
    {
        internal readonly SessionModuleTestContext S = new();
        internal readonly RuntimeWorldObjectMarkerRegistry Runtime = new();
        internal readonly WorldSynchronization Sync;
        internal Fixture()
        {
            var relations = new DiplomacyRelationRegistry(); var world = new WorldConfigurationRegistry();
            var protection = new AIRsLight.ClashOfRim.Network.Modules.Raids.RaidProtection(S.Ledger, new(), new(), () => S.Configuration, NullLogger.Instance);
            var markers = new WorldMarkerDelivery(S.Players, S.Presence, relations, S.Snapshots, world, new(new ServerPluginRegistry(), new()), Runtime,
                new(S.Ledger, S.Snapshots), protection, new(S.Players, S.Snapshots), () => S.Configuration);
            Sync = new(new(S.Tokens, S.Sessions, relations, new(), S.Ledger, () => S.Configuration.PvpEnabled), S.Mutations, S.Timeouts, Runtime, markers);
        }
        internal void Seed() { Runtime.ReplaceForOwner("user", "colony", "old", [Marker("old")], Now); Runtime.ReplaceForOwner("other", "colony", "other", [Marker("other")], Now); }
        internal string Evidence() => JsonSerializer.Serialize(Runtime.ListVisibleMarkers("viewer", Now));
    }
    private sealed class ObservedList(IReadOnlyList<RuntimeWorldObjectMarkerDto> items, Action onEnumerate) : IReadOnlyList<RuntimeWorldObjectMarkerDto>
    {
        public int Count => items.Count;
        public RuntimeWorldObjectMarkerDto this[int index] => items[index];
        public IEnumerator<RuntimeWorldObjectMarkerDto> GetEnumerator() { onEnumerate(); return items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static RuntimeWorldObjectMarkerDto Marker(string id, int[]? path = null) => new(id, "Caravan", "Caravan", 0, "caravan", path);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
