using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

internal static class ClientWorldMarkersTests
{
    private static int scenarios;
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture; scenarios = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Freeze(); Empty(); Queue(); Query(); Failures(); Reentry();
                foreach (string change in new[] { "server", "user", "colony", "auth", "login", "generation", "world-id", "reset", "unavailable", "game", "world", "snapshot" })
                {
                    Lifetime(change);
                    foreach (string stage in new[] { "diagnostic", "summary", "status", "available", "apply" }) During(change, stage);
                }
            }
            Console.WriteLine($"  world markers: {scenarios} scenarios checked");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static ModWorldMapMarkerDto Marker(string id = "marker", int layer = 0) => new()
    {
        MarkerId = id, Kind = "TradeableColony", OwnerUserId = "bob", OwnerColonyId = "home", SnapshotId = "snap", MapId = "map",
        WorldObjectId = "object", Tile = 10, TileLayerId = layer, Label = "基地 Î", CanRaid = true, CanReinforce = true, CanTrade = true,
        PathTiles = new() { 10, 11 }, Appearance = new() { Mode = "Color", ColorHex = "#123456" }
    };
    private static ModWorldMapMarkerDeliveryDto Data(params ModWorldMapMarkerDto[] markers) => new()
    { UserId = "alice", GeneratedAtUtc = "source-time", GiftsEnabled = false, PvpEnabled = false, Markers = markers.ToList() };
    private static void Freeze()
    {
        var f = new Fixture(); var input = Data(Marker()); string expected = ClientProtocolJson.Serialize(input.Markers);
        f.Flow.Observe(input); input.Markers[0].Appearance!.Mode = "borrowed"; input.Markers[0].PathTiles![0] = 999; input.Markers.Clear();
        var read = f.Flow.Snapshot; Check(ClientProtocolJson.Serialize(read) == expected && !f.Flow.GiftsEnabled && !f.Flow.PvpEnabled, "Delivery was borrowed or lost capabilities.");
        read[0].Appearance!.Mode = "reader"; read[0].PathTiles!.Clear(); read.Clear();
        f.MutateArguments = true; f.Run();
        Check(ClientProtocolJson.Serialize(f.Flow.Snapshot) == expected && f.Applied.Single().User == "alice", "Presentation/application borrowed cache or current user.");
        Check(f.Sites.Block(10, 0)?.Appearance?.ColorHex == "#123456", "Occupied catalog did not receive the frozen marker delta."); scenarios++;
    }
    private static void Empty()
    {
        var f = new Fixture(); f.Flow.ApplyCached(); Check(f.Applied.Count == 0, "Uninitialized cache deleted saved world objects.");
        f.Flow.Observe(Data(Marker()), true); f.HasWorld = false; f.Flow.Observe(Data(), true);
        Check(f.Applied.Count == 1 && f.Flow.Snapshot.Count == 0, "No-world delivery was applied or not retained.");
        f.Sites.World = new(); f.Sites.Ports.GameOwner = new(); f.HasWorld = true; f.Flow.ApplyCached();
        Check(f.Applied.Count == 2 && f.Applied.Last().Markers.Count == 0, "Authoritative empty cache did not clear new-world projection.");
        Check(f.Sites.Block(10, 0) is not null, "Empty marker delta incorrectly cleared occupied reservations.");
        f.Flow.Observe(null, true); Check(f.Flow.GiftsEnabled && f.Flow.PvpEnabled && f.Applied.Last().Markers.Count == 0, "Null delivery default semantics changed.");
        f.Flow.Observe(new() { Markers = null! }, true); Check(f.Flow.Snapshot.Count == 0, "Null marker list was not normalized."); scenarios++;
    }
    private static void Queue()
    {
        var f = new Fixture(); f.Flow.Observe(Data(Marker("old"))); var old = f.Pending[0];
        f.Flow.Observe(Data(Marker("new"))); var latest = f.Pending[1]; latest.Run(); old.Run(); latest.Run();
        Check(f.Applied.Count == 1 && f.Applied[0].Markers[0].MarkerId == "new", "Older/repeated queue projection overwrote latest view.");
        f.Flow.Observe(Data(Marker("discard"))); var discarded = f.Pending.Last(); discarded.Discard(); discarded.Run();
        Check(f.Applied.Count == 1, "Discarded projection ran.");
        f.Flow.ApplyCached(); Check(f.Applied.Last().Markers[0].MarkerId == "discard", "Discarded queue lost retryable cache."); scenarios++;
    }
    private static void Lifetime(string change)
    {
        var f = new Fixture(); f.Flow.Observe(Data(Marker())); f.Change(change); f.Run();
        Check(f.Applied.Count == (change == "snapshot" ? 1 : 0), "Old queued work crossed source boundary: " + change);
        bool retained = change is "game" or "world" or "snapshot" or "unavailable";
        Check((f.Flow.Snapshot.Count == 1) == retained && f.Flow.GiftsEnabled == !retained, "Cache lifetime differs from account/server-world lifetime: " + change);
        f.Flow.ApplyCached();
        Check(f.Applied.Count == (change == "snapshot" ? 2 : change is "game" or "world" ? 1 : 0), "Cached replay used old engine owner or cleared data: " + change); scenarios++;
    }
    private static void During(string change, string stage)
    {
        var f = new Fixture(); f.Once(stage, () => f.Change(change)); f.Flow.Observe(Data(Marker()), true);
        Check(f.Applied.Count == (change == "snapshot" || stage == "apply" ? 1 : 0), "Projection continued after callback changed source: " + stage + "/" + change);
        Check(f.Diagnostics.Count == (change == "snapshot" ? 2 : 1), "Old projection logged completion for a new owner: " + stage + "/" + change); scenarios++;
    }
    private static void Reentry()
    {
        foreach (string stage in new[] { "diagnostic", "summary", "status", "available", "apply" })
        {
            var f = new Fixture(); f.Once(stage, () => f.Flow.Observe(Data(Marker("new")), true));
            f.Flow.Observe(Data(Marker("old")), true);
            Check(f.Flow.Snapshot.Single().MarkerId == "new" && f.Applied.Last().Markers.Single().MarkerId == "new", "Old projection resumed after same-world replacement: " + stage);
            Check(f.Applied.Count == (stage == "apply" ? 2 : 1), "Reentrant projection produced extra engine calls."); scenarios++;
        }
    }
    private static void Failures()
    {
        foreach (string stage in new[] { "diagnostic", "enqueue", "summary", "status", "available", "apply" })
        {
            var f = new Fixture(); f.Once(stage, () => throw new InvalidOperationException("injected"));
            try { f.Flow.Observe(Data(Marker())); f.Run(); throw new Exception("Missing failure: " + stage); }
            catch (InvalidOperationException ex) when (ex.Message == "injected") { }
            Check(f.Flow.Snapshot.Count == 1, "Projection failure erased accepted cache.");
            f.Flow.ApplyCached(); Check(f.Applied.Last().Markers.Single().MarkerId == "marker", "Failed projection could not replay cache."); scenarios++;
        }
    }
    private static void Query()
    {
        var f = new Fixture(); var sameLayer = Marker("same"); var otherLayer = Marker("other", 7); var self = Marker("self"); self.OwnerUserId = "alice"; self.OwnerColonyId = "colony";
        var noWorldObject = Marker("support-only"); noWorldObject.WorldObjectId = "";
        var data = Data(sameLayer, otherLayer, self, noWorldObject); data.PvpEnabled = true; f.Flow.Observe(data);
        Check(f.Flow.FindTargets(10, 7, ClientColonyTargetKind.Reinforcement).Single().MarkerId == "other", "Target selection crossed planet layers.");
        var raid = f.Flow.FindTargets(10, 0, ClientColonyTargetKind.Raid);
        Check(raid.Single().MarkerId == "same" && f.Flow.FindTargets(10, 0, ClientColonyTargetKind.Reinforcement).Count == 2, "Raid/support eligibility lost required fields or self exclusion.");
        raid[0].Appearance!.Mode = "caller"; Check(f.Flow.Snapshot[0].Appearance!.Mode == "Color", "Target query exposed cached DTO.");
        data.PvpEnabled = false; f.Flow.Observe(data); Check(f.Flow.FindTargets(10, 0, ClientColonyTargetKind.Raid).Count == 0, "Disabled PvP still offered raid target.");
        foreach (var mutate in new Action<ModWorldMapMarkerDto>[] { m => m.OwnerUserId = " ", m => m.OwnerColonyId = null, m => m.SnapshotId = "", m => m.MapId = null, m => m.CanReinforce = false })
        {
            var invalid = Marker(); mutate(invalid); f.Flow.Observe(Data(invalid));
            Check(f.Flow.FindTargets(10, 0, ClientColonyTargetKind.Reinforcement).Count == 0, "Incomplete target was offered."); scenarios++;
        }
        f.Flow.Observe(Data(Marker())); f.Sites.Ports.Snapshot = "";
        Check(f.Flow.FindTargets(10, 0, ClientColonyTargetKind.Reinforcement).Count == 0, "No-snapshot caller received targets."); scenarios++;
    }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private sealed class Fixture : IClientWorldMarkersRuntime, IClientWorldMarkersPresentation
    {
        internal readonly ClientOccupiedColonySitesTests.Fixture Sites = new();
        internal readonly ClientWorldMarkers Flow;
        internal readonly List<(Action Run, Action Discard)> Pending = new();
        internal readonly List<(string User, List<ModWorldMapMarkerDto> Markers)> Applied = new();
        internal readonly List<string> Diagnostics = new();
        internal bool HasWorld = true, MutateArguments;
        internal Action<string>? Hook;
        internal Fixture() => Flow = new(Sites.Session, Sites, Sites.Cache, this, this);
        public IClientWorldMarkersSession Bind() => new Bound(this, Sites.Ports.GameOwner, Sites.World);
        public string Summary(IReadOnlyCollection<ModWorldMapMarkerDto> markers) { Mutate(markers); Hook?.Invoke("summary"); return "markers=" + markers.Count; }
        public void Status(string text) => Hook?.Invoke("status");
        public void Diagnose(string text, bool warning) { Diagnostics.Add(text); Hook?.Invoke("diagnostic"); }
        internal void Run() { foreach (var work in Pending.ToArray()) work.Run(); Pending.Clear(); }
        internal void Once(string stage, Action action) => Hook = value => { if (value == stage) { Hook = null; action(); } };
        internal void Change(string change) { if (change == "reset") Flow.Reset(); else Sites.Change(change); }
        private void Mutate(IEnumerable<ModWorldMapMarkerDto> markers)
        { if (MutateArguments) foreach (var m in markers) { m.Label = "callback"; m.PathTiles?.Clear(); if (m.Appearance is not null) m.Appearance.ColorHex = "changed"; } }
        private sealed record Bound(Fixture F, object Game, object World) : IClientWorldMarkersSession
        {
            public bool IsCurrent => ReferenceEquals(Game, F.Sites.Ports.GameOwner) && ReferenceEquals(World, F.Sites.World);
            public bool HasWorldObjects { get { F.Hook?.Invoke("available"); return F.HasWorld; } }
            public void Enqueue(Action action, Action discard) { F.Hook?.Invoke("enqueue"); F.Pending.Add((action, discard)); }
            public ClientWorldMarkersApplied Apply(IReadOnlyCollection<ModWorldMapMarkerDto> markers, string user)
            {
                F.Applied.Add((user, ClientProtocolJson.Copy(markers.ToList()))); F.Mutate(markers); F.Hook?.Invoke("apply");
                return new(true, "applied");
            }
        }
    }
}
