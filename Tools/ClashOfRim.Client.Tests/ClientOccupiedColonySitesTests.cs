using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientOccupiedColonySitesTests
{
    private static int scenarios;
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture; var ui = CultureInfo.CurrentUICulture; scenarios = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Catalog(); FrozenValues(); Markers();
                foreach (string change in Changes) { CacheLifetime(change); MergeLifetime(change); QueryLifetime(change); }
                foreach (string stage in new[] { "capture", "appearance", "proxy" })
                {
                    foreach (string change in Changes) UpdateLifetime(stage, change);
                    ReentrantUpdate(stage); FailureRetry(stage);
                }
                QueryReplacement(); EmptyAndReentrantMerge();
            }
            Console.WriteLine($"  occupied colony sites: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; ClientSessionWork.Reset(); }
    }
    private static readonly string[] Changes = { "user", "auth", "server", "colony", "login", "generation", "world-id", "reset", "unavailable", "game", "world", "snapshot" };
    private static bool KeepsCache(string change) => change is "game" or "world" or "snapshot";
    private static bool KeepsOperation(string change) => change == "snapshot";
    private static void Catalog()
    {
        var f = new Fixture();
        f.Replace(Sites(Site(10, 0, "surface"), Site(10, 7, "underground"), Site(10, 0, "winner"), Site(-1, 0, "invalid")));
        Check(f.Block(10, 0)?.UserId == "winner" && f.Block(10, 7)?.UserId == "underground", "Tile/layer key or duplicate precedence changed.");
        Check(f.Block(11, 0)?.UserId == "winner" && f.Block(12, 0) is null, "Adjacency changed.");
        Check(f.Block(10, 3) is null && f.Block(10, 9) is null && f.Block(-1, 0) is null && f.Block(30, 0) is null, "Invalid/space query reserved.");
        Check(f.Geometry.All(pair => pair.Layer is 0 or 7), "Invalid layer reached geometry.");
        f.Replace(Sites(Site(15, 7, "only-other-layer")));
        Check(f.Block(15, 0) is null && f.Block(15, 7) is not null, "Another layer blocked the surface.");
        f.Replace(Sites(Site(25, 0), Site(25, 7)));
        Check(f.Block(25, 0) is null && f.Block(25, 7) is not null, "Tile bounds were borrowed from the surface.");
        f.Replace(null); Check(f.Block(25, 7) is null && f.WorldId == "world-a", "Null catalog did not clear sites or erased identity.");
        f.Replace(Sites(Site(10, 0))); f.Replace(Array.Empty<ModPlayerColonySiteDto>());
        Check(f.Block(10, 0) is null, "Empty site list retained old reservations.");
        scenarios++;
    }
    private static void FrozenValues()
    {
        var f = new Fixture(); var own = Site(5, 0, "alice"); own.ColonyId = "colony";
        var source = Sites(own, Site(10, 0, "Bob"), Site(12, 0, "Bob"), Site(14, 0, "bob"), Site(16, 0, " "));
        string expected = ClientProtocolJson.Serialize(own);
        f.Hook = stage =>
        {
            if (stage != "appearance") return;
            own.Label = "changed"; own.Appearance!.ColorHex = "changed";
            source.Clear();
        };
        f.Replace(source, appearance => appearance!.ColorHex = "observer changed");
        Check(f.WorldId == "world-a" && f.Appearances.Count == 1 && f.Appearances[0]!.ColorHex == "#123456", "Site replacement changed world identity or borrowed appearance.");
        Check(f.Proxies.SequenceEqual(new[] { "Bob", "bob" }), "Remote owners lost ordinal uniqueness/order.");
        var result = f.Block(5, 0)!;
        Check(ClientProtocolJson.Serialize(result) == expected, "Catalog fields were lost or mutated by a callback.");
        result.Label = "caller changed"; result.Appearance!.Mode = "caller changed";
        Check(ClientProtocolJson.Serialize(f.Block(5, 0)) == expected, "Query exposed cached DTO.");
        f.Hook = null;
        var otherColony = Site(5, 0, "alice"); otherColony.ColonyId = "other";
        f.Replace(Sites(otherColony, Site(6, 0, "ALICE")));
        Check(f.Appearances.Count == 1 && f.Proxies.Last() == "ALICE", "Own colony/name comparison changed.");
        scenarios++;
    }
    private static void Markers()
    {
        var f = new Fixture(); f.Replace(Sites(Site(3, 0, "retained")));
        f.Proxies.Clear();
        var source = new[] { Marker(10, 0), Marker(10, 7), Marker(10, 0, "winner"), Marker(-1, 0), Marker(6, 0, " "), Marker(15, 0) };
        source[5].Kind = "tradeablecolony";
        f.Cache.Merge(source);
        var hit = f.Block(10, 0)!;
        Check(hit.UserId == "winner" && hit.ColonyId == "" && hit.MapUniqueId == "map" && hit.WorldObjectId == "object"
            && hit.Label == "基地 Î" && hit.Appearance!.Mode == "Icon" && hit.FactionName is null, "Marker projection lost fields/defaults.");
        source[2].Appearance!.Mode = "mutated"; source[2].Tile = 19;
        Check(f.Block(10, 0)?.Appearance?.Mode == "Icon" && f.Block(10, 7) is not null && f.Block(3, 0)?.UserId == "retained", "Merge borrowed input, crossed layers or erased old sites.");
        Check(f.Block(6, 0) is null && f.Block(15, 0) is null && f.Proxies.Count == 0 && f.Appearances.Count == 0, "Invalid marker or merge side effect accepted.");
        f.Cache.Merge(Array.Empty<ModWorldMapMarkerDto>()); Check(f.Block(10, 0) is not null, "Empty delta erased catalog.");
        scenarios++;
    }
    private static void CacheLifetime(string change)
    {
        var f = new Fixture(); f.Replace(Sites(Site(10, 0))); f.Change(change);
        Check((f.Block(10, 0) is not null) == KeepsCache(change), "Wrong cache lifetime: " + change);
        scenarios++;
    }
    private static void MergeLifetime(string change)
    {
        var f = new Fixture(); f.Replace(Sites(Site(3, 0)));
        IEnumerable<ModWorldMapMarkerDto> Incoming() { yield return Marker(10, 0); f.Change(change); yield return Marker(15, 7); }
        f.Cache.Merge(Incoming());
        Check((f.Block(10, 0) is not null) == KeepsOperation(change), "Lazy markers adopted new scope: " + change);
        Check((f.Block(3, 0) is not null) == KeepsCache(change), "Merge erased valid old catalog: " + change);
        scenarios++;
    }
    private static void QueryLifetime(string change)
    {
        foreach (string stage in new[] { "valid", "neighbor" })
        {
            var f = new Fixture(); f.Replace(Sites(Site(10, 0)));
            f.Once(stage, () => f.Change(change));
            Check((f.Block(10, 0) is not null) == KeepsOperation(change), "Query published after " + stage + ": " + change);
            scenarios++;
        }
    }
    private static void UpdateLifetime(string stage, string change)
    {
        var f = new Fixture(); var own = Site(5, 0, "alice"); own.ColonyId = "colony";
        f.Once(stage, () => f.Change(change));
        f.Replace(Sites(own, Site(10, 0, "bob"), Site(15, 0, "charlie")));
        int expectedProxies = KeepsOperation(change) ? 2 : stage == "proxy" ? 1 : 0;
        Check(f.Proxies.Count == expectedProxies, "Stale update continued proxy effects: " + stage + "/" + change);
        bool expectedHit = KeepsCache(change) && (stage != "capture" || KeepsOperation(change));
        Check((f.Block(10, 0) is not null) == expectedHit, "Wrong cache publication boundary: " + stage + "/" + change);
        scenarios++;
    }
    private static void ReentrantUpdate(string stage)
    {
        var f = new Fixture(); var own = Site(5, 0, "alice"); own.ColonyId = "colony";
        f.Once(stage, () => f.Replace(Sites(Site(15, 0, "replacement"))));
        f.Replace(Sites(own, Site(10, 0, "old-owner"), Site(12, 0, "must-not-run")));
        Check(f.Block(15, 0)?.UserId == "replacement" && f.Block(10, 0) is null, "Old update replaced reentrant catalog: " + stage);
        Check(!f.Proxies.Contains("must-not-run"), "Old update resumed after replacement: " + stage);
        f.Replace(Sites(Site(10, 0, "retry")));
        Check(f.Block(10, 0)?.UserId == "retry", "Old completion poisoned next update.");
        scenarios++;
    }
    private static void FailureRetry(string stage)
    {
        var f = new Fixture(); var own = Site(5, 0, "alice"); own.ColonyId = "colony";
        f.Once(stage, () => throw new InvalidOperationException("injected"));
        try { f.Replace(Sites(own, Site(10, 0))); throw new Exception("Missing injected failure."); }
        catch (InvalidOperationException ex) when (ex.Message == "injected") { }
        Check((f.Block(10, 0) is not null) == (stage != "capture"), "Callback failure rolled back or partly published cache: " + stage);
        f.Replace(Sites(Site(15, 0))); Check(f.Block(15, 0) is not null && f.Block(10, 0) is null, "Update did not recover.");
        scenarios++;
    }
    private static void QueryReplacement()
    {
        foreach (string stage in new[] { "valid", "neighbor" })
        {
            var f = new Fixture(); f.Replace(Sites(Site(10, 0, "old")));
            f.Once(stage, () => f.Cache.Merge(new[] { Marker(10, 0, "new") }));
            Check(f.Block(10, 0) is null && f.Block(10, 0)?.UserId == "new", "Query returned obsolete view in the same world.");
            scenarios++;
        }
    }
    private static void EmptyAndReentrantMerge()
    {
        var f = new Fixture(); var own = Site(5, 0, "alice"); own.ColonyId = "colony";
        f.Once("appearance", () => f.Cache.Merge(Array.Empty<ModWorldMapMarkerDto>()));
        f.Replace(Sites(own, Site(10, 0, "bob")));
        Check(f.Proxies.SequenceEqual(new[] { "bob" }), "Empty delta interrupted catalog effects.");
        IEnumerable<ModWorldMapMarkerDto> Incoming()
        {
            yield return Marker(15, 0);
            f.Replace(Sites(Site(3, 0, "replacement")));
            yield return Marker(10, 0);
        }
        f.Cache.Merge(Incoming());
        Check(f.Block(3, 0)?.UserId == "replacement" && f.Block(10, 0) is null, "Lazy delta overwrote a newer same-world view.");
        scenarios++;
    }
    private static List<ModPlayerColonySiteDto> Sites(params ModPlayerColonySiteDto[] sites) => sites.ToList();
    private static ModPlayerColonySiteDto Site(int tile, int layer, string user = "remote") => new()
    {
        Tile = tile, TileLayerId = layer, UserId = user, ColonyId = "colony-Î", WorldObjectId = "object", MapUniqueId = "map", Label = "基地 Î", FactionName = "faction",
        Appearance = new() { Mode = "Icon", IconDefName = "icon", ColorDefName = "color", ColorHex = "#123456" }
    };
    private static ModWorldMapMarkerDto Marker(int tile, int layer, string user = "remote") => new()
    {
        Kind = "TradeableColony", Tile = tile, TileLayerId = layer, OwnerUserId = user, WorldObjectId = "object", MapId = "map", Label = "基地 Î", Appearance = Site(0, 0).Appearance
    };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal sealed class Fixture : IClientWorldIdentity, IClientWorldConfigurationStore, IClientOccupiedColonySitesRuntime, IClientWorldConfigurationRuntime
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientOccupiedColonySites Cache;
        internal object World = new();
        public string WorldId { get; set; } = "world-a";
        internal Action<string>? Hook;
        internal readonly List<string> Proxies = new();
        internal readonly List<ModColonyAppearanceDto?> Appearances = new();
        internal readonly List<(int Tile, int Other, int Layer)> Geometry = new();
        internal Fixture(IClientOccupiedColonySitesRuntime? runtime = null)
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new(Ports));
            Cache = new(Session, this, runtime ?? this);
        }
        internal void Replace(IEnumerable<ModPlayerColonySiteDto>? source, Action<ModColonyAppearanceDto?>? observe = null)
        {
            Cache.Replace(Incoming(), appearance =>
            {
                Appearances.Add(appearance is null ? null : ClientProtocolJson.Copy(appearance));
                Hook?.Invoke("appearance"); observe?.Invoke(appearance);
            });
            IEnumerable<ModPlayerColonySiteDto> Incoming()
            {
                Hook?.Invoke("capture");
                foreach (var site in source ?? Enumerable.Empty<ModPlayerColonySiteDto>()) yield return site;
            }
        }
        public void Remember(string id) { WorldId = id; Hook?.Invoke("remember"); }
        public IClientOccupiedColonySitesSession Bind() => new Bound(this, Ports.GameOwner, World);
        IClientWorldConfigurationSession IClientWorldConfigurationRuntime.Bind() => new Bound(this, Ports.GameOwner, World);
        internal ModPlayerColonySiteDto? Block(int tile, int layer) => Cache.IsBlocked(tile, layer, out var site) ? site : null;
        internal void Once(string stage, Action action) => Hook = value => { if (value == stage) { Hook = null; action(); } };
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "other"; break;
                case "auth": Ports.Auth = "new-auth"; break;
                case "server": Ports.Server = "http://other"; break;
                case "colony": Ports.Colony = "new-colony"; break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new-session" }); break;
                case "generation": ClientSessionWork.Reset(); break;
                case "world-id": WorldId = "new-world"; break;
                case "reset": Cache.Reset(); break;
                case "unavailable": Session.BeginUnavailable(Ports.Auth, null); break;
                case "game": Ports.GameOwner = new(); break;
                case "world": World = new(); break;
                case "snapshot": Ports.Snapshot = "advanced"; break;
                default: throw new InvalidOperationException(change);
            }
        }
        private sealed record Bound(Fixture Owner, object Game, object World) : IClientOccupiedColonySitesSession, IClientWorldConfigurationSession
        {
            public bool IsCurrent => ReferenceEquals(Game, Owner.Ports.GameOwner) && ReferenceEquals(World, Owner.World);
            public bool CanReserve(int tile, int layer) { Owner.Hook?.Invoke("valid"); return tile >= 0 && (layer == 0 ? tile < 20 : layer == 7 && tile < 30); }
            public bool IsNeighborOrSame(int tile, int other, int layer)
            { Owner.Geometry.Add((tile, other, layer)); Owner.Hook?.Invoke("neighbor"); return Math.Abs(tile - other) <= 1; }
            public void EnsureProxy(string user) { Owner.Proxies.Add(user); Owner.Hook?.Invoke("proxy"); }
            public ClientWorldFeatureNames? ApplyFeatures(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyFactions(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyRoads(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
            public void ApplyExtensions(ModWorldConfigurationDto configuration, string userId, bool worldState) => throw new NotSupportedException();
            public void ApplyObjectLabels(ModWorldConfigurationDto configuration) => throw new NotSupportedException();
        }
    }
}
