using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class ClientWorldSynchronizationRuntimeTests
{
    private static readonly List<WorldObject> objects = new();
    private static readonly HashSet<WorldObject> detached = new();
    private static readonly List<PlanetTile> nodes = new();
    private static bool moving, found;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld; var culture = CultureInfo.CurrentCulture;
        var harmony = new Harmony("ClashOfRim.Tests.WorldSynchronizationRuntime");
        try
        {
            void Patch(Type type, string property, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, property), prefix: new HarmonyMethod(typeof(ClientWorldSynchronizationRuntimeTests), prefix));
            Patch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.AllWorldObjects), nameof(Objects));
            Patch(typeof(WorldObject), nameof(WorldObject.Spawned), nameof(Spawned));
            Patch(typeof(Caravan), nameof(Caravan.Label), nameof(Label));
            Patch(typeof(PlanetLayer), nameof(PlanetLayer.TilesCount), nameof(Count));
            Patch(typeof(PlanetLayer), nameof(PlanetLayer.Selected), nameof(Selected));
            Type patherType = AccessTools.Field(typeof(Caravan), "pather").FieldType;
            Type pathType = AccessTools.Field(patherType, "curPath").FieldType;
            Patch(patherType, "Moving", nameof(Moving)); Patch(pathType, "Found", nameof(Found)); Patch(pathType, "NodesLeftCount", nameof(NodeCount));
            harmony.Patch(AccessTools.Method(pathType, "Peek"), prefix: new HarmonyMethod(typeof(ClientWorldSynchronizationRuntimeTests), nameof(Peek)));
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); objects.Clear(); detached.Clear(); nodes.Clear();
                Current.Game = null; Current.CreatingWorld = null; var runtime = new ClientWorldSynchronizationRuntime(); var menu = runtime.Bind();
                Check(menu.IsCurrent && !menu.HasWorldObjects && menu.CaptureObjects().Count == 0, "Menu runtime capture accessed a missing world.");
                var live = New<Game>(); var world = New<World>(); live.World = world; Current.Game = live;
                var player = New<Faction>(); player.def = new FactionDef { isPlayer = true };
                world.factionManager = New<FactionManager>(); Set(world.factionManager, "ofPlayer", player); world.worldObjects = New<WorldObjectsHolder>();
                var surface = ClientOccupiedColonySitesRuntimeTests.Layer(0, 20); var underground = ClientOccupiedColonySitesRuntimeTests.Layer(7, 40);
                world.grid = New<WorldGrid>(); Set(world.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = surface, [7] = underground }); Set(world.grid, "surface", surface);
                var caravan = Add<Caravan>(1, "Caravan", 25, 7, player); var shuttle = Add<WorldObject>(2, "MyShuttle", 10, 0, player);
                Add<WorldObject>(3, "TransportPodFleet", 11, 0, player); Add<TravelingFixture>(4, "Special", 12, 0, player);
                Add<Settlement>(5, "Caravan", 10, 0, player); Add<RemoteRuntimeWorldObject>(6, "Caravan", 10, 0, player);
                Add<RemoteColonyMapParent>(7, "Caravan", 10, 0, player); Add<WorldObject>(8, "Unrelated", 10, 0, player);
                Add<WorldObject>(9, "Caravan", 20, 0, player); Add<WorldObject>(10, "Shuttle", 10, 99, player);
                Add<WorldObject>(11, "Shuttle", -1, 7, player); Add<WorldObject>(12, "Shuttle", 10, 0, New<Faction>());
                var dead = Add<WorldObject>(13, "Shuttle", 10, 0, player); Set(dead, "destroyed", true);
                detached.Add(Add<WorldObject>(14, "Shuttle", 10, 0, player));
                Add<RemoteSessionMapParent>(15, "Caravan", 10, 0, player);
                Add<WorldObject>(16, "Shuttle", 10, 0, player).def = null!;
                var foreign = New<Faction>(); foreign.def = new FactionDef { isPlayer = false }; Add<Caravan>(17, "Caravan", 10, 0, foreign);
                var pather = RuntimeHelpers.GetUninitializedObject(patherType); var path = RuntimeHelpers.GetUninitializedObject(pathType);
                Set(caravan, "pather", pather); Set(pather, "curPath", path); moving = found = true; nodes.AddRange(new[] { new PlanetTile(25, 7), new PlanetTile(26, 7) });
                var bound = runtime.Bind(); var batch = bound.CaptureObjects().OrderBy(v => v.WorldObjectId, StringComparer.Ordinal).ToArray();
                Check(batch.Length == 4 && batch.Select(v => v.Kind).OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(new[] { "Caravan", "Shuttle", "TransportPod", "WorldObject" }), "Runtime eligibility/kind mapping diverged from batch signature source.");
                var captured = batch.Single(v => v.Kind == "Caravan");
                Check(captured.Tile == 25 && captured.TileLayerId == 7 && captured.Label == "商队 Î" && captured.PathTiles!.SequenceEqual(new[] { 25, 26 }), "Non-surface tile or path borrowed surface geometry.");
                nodes[0] = new PlanetTile(10, 0); Check(bound.CaptureObjects().Single(v => v.Kind == "Caravan").PathTiles is null, "Cross-layer route was flattened into one-layer wire path.");
                nodes[0] = new PlanetTile(25, 7); nodes[1] = new PlanetTile(40, 7); Check(bound.CaptureObjects().Single(v => v.Kind == "Caravan").PathTiles is null, "Invalid path node survived capture.");
                nodes[1] = new PlanetTile(26, 7); moving = false; Check(bound.CaptureObjects().Single(v => v.Kind == "Caravan").PathTiles is null, "Stationary caravan retained old movement path.");
                moving = true; found = false; Check(bound.CaptureObjects().Single(v => v.Kind == "Caravan").PathTiles is null, "Unfound path survived.");
                found = true; nodes.RemoveAt(1); Check(bound.CaptureObjects().Single(v => v.Kind == "Caravan").PathTiles is null, "One-node path survived.");
                var locations = bound.CaptureCaravans(); Check(locations.Count == 1 && locations.Single().TileLayerId == 7 && locations.Single().PathTiles is null, "Arrival signature incorporated non-caravans or paths.");
                Check(captured.PathTiles!.SequenceEqual(new[] { 25, 26 }), "Captured path shared engine nodes.");
                live.World = New<World>(); Check(!bound.IsCurrent && !menu.IsCurrent, "Runtime source adopted replacement world.");
                live.World = world; Current.Game = New<Game>(); Current.Game.World = world; Check(!bound.IsCurrent, "Runtime source adopted replacement game.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating; CultureInfo.CurrentCulture = culture; objects.Clear(); detached.Clear(); nodes.Clear(); }
    }
    private static T Add<T>(int id, string definition, int tile, int layer, Faction faction) where T : WorldObject
    {
        var value = New<T>(); value.def = new WorldObjectDef { defName = definition, label = definition }; Set(value, "ID", id);
        Set(value, "tile", new PlanetTile(tile, layer)); Set(value, "factionInt", faction); objects.Add(value); return value;
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string name, object value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
    private static bool Objects(ref List<WorldObject> __result) { __result = objects; return false; }
    private static bool Spawned(WorldObject __instance, ref bool __result) { __result = !detached.Contains(__instance); return false; }
    private static bool Label(ref string __result) { __result = "商队 Î"; return false; }
    private static bool Count(PlanetLayer __instance, ref int __result) { __result = __instance.Tiles.Count; return false; }
    private static bool Selected() => throw new InvalidOperationException("Runtime capture read UI-selected layer.");
    private static bool Moving(ref bool __result) { __result = moving; return false; }
    private static bool Found(ref bool __result) { __result = found; return false; }
    private static bool NodeCount(ref int __result) { __result = nodes.Count; return false; }
    private static bool Peek(int __0, ref PlanetTile __result) { __result = nodes[__0]; return false; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class TravelingFixture : WorldObject { }
}
