using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class ClientWorldBaselineRuntimeTests
{
    private static readonly Dictionary<Game, List<Map>> maps = new();
    private static readonly Dictionary<Map, MapParent> parents = new();
    private static readonly Dictionary<WorldObjectsHolder, List<WorldObject>> objects = new();
    private static readonly HashSet<Map> homes = new();
    private static readonly List<IExposable> serialized = new();
    private static Action<IExposable>? onSerialize;
    private static Action? onCenter;
    private static bool odyssey;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld; var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.WorldBaselineCaptureRuntime");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientWorldBaselineRuntimeTests), prefix));
        try
        {
            Patch(AccessTools.Method(typeof(ClientWorldNameRuntime), nameof(ClientWorldNameRuntime.ReadCurrentGameLanguage)), nameof(Language));
            Patch(AccessTools.PropertyGetter(typeof(Game), nameof(Game.Maps)), nameof(Maps));
            Patch(AccessTools.PropertyGetter(typeof(Game), nameof(Game.CurrentMap)), nameof(CurrentMap));
            Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.Parent)), nameof(Parent));
            Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.Tile)), nameof(MapTile));
            Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.IsPlayerHome)), nameof(Home));
            Patch(AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.Map)), nameof(ParentMap));
            Patch(AccessTools.PropertyGetter(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.AllWorldObjects)), nameof(Objects));
            Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.AverageTileSize)), nameof(TileSize));
            Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.TilesCount)), nameof(TileCount));
            Patch(AccessTools.PropertyGetter(typeof(SurfaceTile), nameof(SurfaceTile.Roads)), nameof(Roads));
            Patch(AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.GetTileCenter), new[] { typeof(int) }), nameof(Center));
            Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Selected)), nameof(Selected));
            Patch(AccessTools.PropertyGetter(typeof(ModsConfig), nameof(ModsConfig.OdysseyActive)), nameof(Odyssey));
            Patch(AccessTools.Method(Scribe.saver.GetType(), "DebugOutputFor"), nameof(Xml));
            foreach (string name in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Read(); Labels(); Substrate();
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating; CultureInfo.CurrentCulture = culture;
            maps.Clear(); parents.Clear(); objects.Clear(); homes.Clear(); serialized.Clear(); onSerialize = null; onCenter = null;
        }
    }
    private static void Read()
    {
        var f = new Fixture(); var bound = new ClientWorldBaselineRuntime().Bind();
        var config = bound.ReadWorld("alice", "colony", new() { ColorHex = "#123456" });
        Check(config.SeedString == "seed" && config.PlanetCoverage == "0.3" && config.GameLanguage == "French", "Engine scalar capture changed invariant values.");
        Check(config.FactionDefNames.SequenceEqual(new[] { "tribe", "tribe" }) && config.Factions.Count == 1 && config.Factions[0].Name == "Other faction", "Faction inputs lost duplicates or player filtering.");
        Check(config.Features.Count == 1 && config.Features[0].DrawCenterX == 1.25f && config.Features[0].Label == "île", "Feature snapshot lost culture/geometry.");
        Check(config.Roads.Count == 1 && config.Roads[0].FromTile == 0 && config.Roads[0].ToTile == 1, "Road capture lost undirected de-duplication.");
        Check(config.WorldObjects.Count == 1 && config.WorldObjects[0].Tile == 3 && config.WorldObjects[0].TileLayerId == 7 && config.WorldObjects[0].Label == "remote", "World object lost its non-surface layer.");
        Check(config.PlayerColonySites.Count == 1, "Expected one layered home site.");
        var site = config.PlayerColonySites.Single();
        Check(site.Tile == 3 && site.TileLayerId == 7 && site.MapUniqueId == "Map_42" && site.UserId == "alice" && site.Appearance?.ColorHex == "#123456", "Valid orbital home rejected by surface size or duplicated.");
        Check(config.TileGeometry!.Layers.Select(l => l.LayerId).SequenceEqual(new[] { 0, 7 })
            && config.TileGeometry.Layers[1].TileCenters.Count == 4 && config.TileGeometry.Layers[1].TileCenters[3].Y == 7, "Geometry used UI-selected layer or lost per-layer centers.");
        f.World.info.factions.Clear();
        Check(bound.ReadWorld("alice", "colony", null).FactionDefNames.SequenceEqual(new[] { "tribe" }), "Empty configured factions failed to use world faction manager.");
        var other = new Fixture(); // Deliberately switch globals, then call the already-bound adapter directly.
        other.World.info.seedString = "successor"; maps[other.Game][0].uniqueID = 999;
        ((Settlement)objects[other.World.worldObjects][1]).Name = "successor settlement";
        var old = bound.ReadWorld("alice", "colony", null);
        Check(!bound.IsCurrent && old.SeedString == "seed" && old.WorldObjects.Single().Label == "remote" && old.PlayerColonySites.Single().MapUniqueId == "Map_42", "Bound runtime reread a successor world's collections.");
        // A callback during native geometry reads must still read only the captured grid.
        Current.Game = f.Game; onCenter = () => Current.Game = other.Game;
        var geometry = ClientWorldBaselineRuntime.ReadWorldTileGeometry(f.World.grid);
        onCenter = null;
        Check(geometry!.Layers.Sum(l => l.TileCenters.Count) == 6, "Geometry switched source grids mid-loop.");
    }
    private static void Labels()
    {
        var f = new Fixture(); var items = objects[f.World.worldObjects];
        Set(items[0], "tile", new PlanetTile(1, 0)); Set(items[1], "tile", new PlanetTile(1, 7));
        ClientWorldConfigurationRuntime.ApplyWorldObjectLabels(new[] { new ModWorldObjectBaselineDto { Tile = 1, TileLayerId = 7, DefName = "Settlement", Label = "orbital name" } });
        Check(((Settlement)items[0]).Name == "home" && ((Settlement)items[1]).Name == "orbital name", "Label projection crossed layers sharing a tile ID.");
    }
    private static void Substrate()
    {
        var f = new Fixture(); var bound = new ClientWorldBaselineRuntime().Bind(); var config = bound.ReadWorld("alice", "colony", null);
        serialized.Clear(); odyssey = false; f.World.landmarks = null;
        var bytes = bound.CaptureSubstrate(config.TileGeometry, out var failure);
        Check(bytes is not null && failure is null && WorldSubstratePackageCodec.TryDecode(bytes!, out var decoded, out _), "Core-only substrate capture failed.");
        WorldSubstratePackageCodec.TryDecode(bytes!, out var package, out _);
        Check(package!.PersistentRandomValue == 42 && package.LandmarksXml == "<landmarks />" && serialized.SequenceEqual(new IExposable[] { f.World.grid, f.World.features }), "Captured wrong world/optional landmark content.");
        odyssey = true; serialized.Clear();
        Check(bound.CaptureSubstrate(config.TileGeometry, out _) is null && serialized.Count == 0, "Odyssey without landmarks was accepted.");
        odyssey = false;
        Scribe.mode = LoadSaveMode.Saving;
        try { Check(bound.CaptureSubstrate(config.TileGeometry, out _) is null && Scribe.mode == LoadSaveMode.Saving, "Busy Scribe was disturbed."); }
        finally { Scribe.mode = LoadSaveMode.Inactive; }
        Check(bound.CaptureSubstrate(null, out _) is null, "Missing geometry was accepted.");
        foreach (int phase in new[] { 1, 2 })
        {
            Current.Game = f.Game; serialized.Clear(); var successor = New<Game>(); successor.World = New<World>();
            onSerialize = _ => { if (serialized.Count == phase) Current.Game = successor; };
            Check(bound.CaptureSubstrate(config.TileGeometry, out _) is null && serialized.Count == phase && ReferenceEquals(Current.Game, successor), "Scribe capture crossed world ownership.");
        }
        Current.Game = f.Game; serialized.Clear(); onSerialize = _ => throw new InvalidOperationException("serialize failed");
        Check(bound.CaptureSubstrate(config.TileGeometry, out var failed) is null && failed!.Contains("serialize failed"), "Scribe failure reported captured substrate.");
        onSerialize = null;
        Check(bound.CaptureSubstrate(config.TileGeometry, out _) is not null, "Capture did not recover after failure.");
    }
    private sealed class Fixture
    {
        internal readonly Game Game = New<Game>();
        internal readonly World World = New<World>();
        internal Fixture()
        {
            Current.CreatingWorld = null; Current.Game = Game; Game.World = World;
            var player = New<Faction>(); player.def = new FactionDef { defName = "Player", isPlayer = true }; player.Name = "Player faction"; player.color = Color.white;
            var faction = New<Faction>(); faction.def = new FactionDef { defName = "tribe" }; faction.Name = "Other faction"; faction.color = new Color(.2f, .3f, .4f);
            World.factionManager = New<FactionManager>(); Set(World.factionManager, "allFactions", new List<Faction> { player, faction }); Set(World.factionManager, "ofPlayer", player);
            World.info = new WorldInfo { seedString = "seed", planetCoverage = .3f, persistentRandomValue = 42, factions = new() { faction.def, faction.def } };
            var surface = Layer(0, 2); var orbital = Layer(7, 4);
            World.grid = New<WorldGrid>(); Set(World.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [7] = orbital, [0] = surface }); Set(World.grid, "surface", surface);
            var road = new RoadDef { defName = "road" };
            ((SurfaceTile)surface.Tiles[0]).potentialRoads = new() { new() { neighbor = new PlanetTile(1, 0), road = road } };
            ((SurfaceTile)surface.Tiles[1]).potentialRoads = new() { new() { neighbor = new PlanetTile(0, 0), road = road } };
            World.features = New<WorldFeatures>(); World.features.features = new() { new() { def = new FeatureDef { defName = "island" }, layer = orbital, name = "île", drawCenter = new Vector3(1.25f, .2f, .3f) } };
            var own = Settlement(player, "home"); var remote = Settlement(faction, "remote");
            World.worldObjects = New<WorldObjectsHolder>(); objects[World.worldObjects] = new() { own, remote };
            var map = New<Map>(); map.uniqueID = 42; parents[map] = own; homes.Add(map); maps[Game] = new() { map };
        }
        private static Settlement Settlement(Faction faction, string label)
        {
            var value = New<Settlement>(); value.def = new WorldObjectDef { defName = "Settlement" }; value.Name = label;
            Set(value, "factionInt", faction); Set(value, "tile", new PlanetTile(3, 7)); return value;
        }
    }
    private static SurfaceLayer Layer(int id, int count)
    {
        var layer = New<SurfaceLayer>(); Set(layer, "layerId", id);
        Set(layer, "tiles", Enumerable.Range(0, count).Select(i => (Tile)new SurfaceTile { tile = new PlanetTile(i, id) }).ToList());
        return layer;
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string field, object? value)
    {
        var member = AccessTools.Field(target.GetType(), field) ?? throw new InvalidOperationException("Missing fixture field " + target.GetType().Name + "." + field
);
        member.SetValue(target, value);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Language(ref string __result) { __result = "French"; return false; }
    private static bool Maps(Game __instance, ref List<Map> __result) { __result = maps.GetValueOrDefault(__instance) ?? new(); return false; }
    private static bool CurrentMap(Game __instance, ref Map? __result) { __result = maps.GetValueOrDefault(__instance)?.FirstOrDefault(); return false; }
    private static bool Parent(Map __instance, ref MapParent __result) { __result = parents[__instance]; return false; }
    private static bool MapTile(Map __instance, ref PlanetTile __result) { __result = parents[__instance].Tile; return false; }
    private static bool Home(Map __instance, ref bool __result) { __result = homes.Contains(__instance); return false; }
    private static bool ParentMap(MapParent __instance, ref Map? __result) { __result = parents.FirstOrDefault(pair => pair.Value == __instance).Key; return false; }
    private static bool Objects(WorldObjectsHolder __instance, ref List<WorldObject> __result) { __result = objects.GetValueOrDefault(__instance) ?? new(); return false; }
    private static bool Roads(SurfaceTile __instance, ref List<SurfaceTile.RoadLink>? __result) { __result = __instance.potentialRoads; return false; }
    private static bool TileCount(PlanetLayer __instance, ref int __result) { __result = __instance.Tiles.Count; return false; }
    private static bool TileSize(ref float __result) { __result = 1.25f; return false; }
    private static bool Center(PlanetLayer __instance, int __0, ref Vector3 __result) { onCenter?.Invoke(); __result = new Vector3(__0, __instance.LayerID, .5f); return false; }
    private static bool Selected() => throw new InvalidOperationException("Capture must not read the selected UI layer.");
    private static bool Odyssey(ref bool __result) { __result = odyssey; return false; }
    private static bool Xml(IExposable __0, ref string __result)
    { serialized.Add(__0); onSerialize?.Invoke(__0); __result = "<saveable><fixture>1.25</fixture></saveable>"; return false; }
}
