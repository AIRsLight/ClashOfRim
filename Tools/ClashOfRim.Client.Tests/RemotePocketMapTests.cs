using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Raids;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

internal static class RemotePocketMapTests
{
    private static readonly List<Map> maps = new();
    private static readonly List<Map> removed = new();
    private static readonly List<Map> cleaned = new();
    private static bool fail;
    private static RemoteSessionMapParent owner = null!;
    internal static void Run()
    {
        var previous = Current.Game;
        var harmony = new Harmony("ClashOfRim.Tests.PocketMaps");
        try
        {
            Current.Game = New<Game>(); Current.Game.World = New<World>();
            AccessTools.Field(typeof(Game), "components").SetValue(Current.Game, new List<GameComponent> { new ClashOfRimGameComponent(Current.Game) });
            Find.World.pocketMaps = new(); Find.World.renderer = New<WorldRenderer>();
            harmony.Patch(AccessTools.PropertyGetter(typeof(Game), nameof(Game.Maps)), prefix: Method(nameof(Maps)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.Map)), prefix: Method(nameof(ParentMap)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Game), nameof(Game.CurrentMap)), prefix: Method(nameof(CurrentMap)));
            harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.DeinitAndRemoveMap)), prefix: Method(nameof(Remove)));
            harmony.Patch(AccessTools.Method(typeof(RemoteSessionThingCleanup), nameof(RemoteSessionThingCleanup.MarkMapThingsForCleanup)), prefix: Method(nameof(Skip)));
            harmony.Patch(AccessTools.Method(typeof(RemoteSessionThingCleanup), nameof(RemoteSessionThingCleanup.DiscardMarkedMapThings)), prefix: Method(nameof(Cleanup)));
            harmony.Patch(AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), prefix: Method(nameof(Skip)));
            harmony.CreateClassProcessor(typeof(RemoteSessionPocketMapRemovalPatch)).Patch();
            owner = new RemoteSessionMapParent { SessionId = "remote" };
            var root = Map(owner, 1); var child = Pocket(root, 2); var nested = Pocket(child, 3);
            var home = Map(new MapParent(), 4); var localPocket = Pocket(home, 5);
            Check(RemoteSessionPocketMapUtility.FindSessionParent(nested) == owner
                && RemoteSessionGlobalStateGuard.IsRemoteMap(nested)
                && RemoteSessionGlobalStateGuard.IsRemoteMapParent(nested.Parent)
                && !RemoteSessionGlobalStateGuard.IsRemoteMap(localPocket), "Nested ownership escaped the remote guard or included local pockets.");
            Check(RemoteSessionPocketMapUtility.MapTree(root).SequenceEqual(new[] { root, child, nested }), "Map tree omitted descendants.");
            var pawn = New<Pawn>(); pawn.def = new ThingDef { defName = "TestPawn" }; pawn.thingIDNumber = 17;
            RemoteSessionPocketMapPawnPatch.Postfix(pawn, nested);
            Check(RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(pawn), "New child pawn missed cleanup identity.");
            pawn.questTags = null;
            ClashOfRimGameComponent.SetActiveRaidBattleSession(new ActiveRaidBattleSession { EventId = "battle", AttackPawnThingIds = new List<string> { pawn.ThingID } });
            RemoteSessionPocketMapPawnPatch.Postfix(pawn, nested);
            Check(!RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(pawn), "Owned attacker became disposable after native portal entry.");
            RemoteSessionPocketMapUtility.ClosePocketMapsForSource(root, "test");
            Check(removed.SequenceEqual(new[] { nested, child }) && cleaned.SequenceEqual(removed)
                && maps.SequenceEqual(new[] { root, home, localPocket }) && Find.World.pocketMaps.Single() == localPocket.Parent,
                "Native close failed child-first ordering or removed local maps.");
            RemoteSessionPocketMapUtility.ClosePocketMapsForSource(root, "repeat");
            Check(removed.Count == 2 && !RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects, "Close repeated effects or leaked scope.");
            var retry = Pocket(root, 6); fail = true;
            try { PocketMapUtility.DestroyPocketMap(retry); throw new Exception("Unload should fail."); }
            catch (InvalidOperationException) { }
            Check(maps.Contains(retry) && Find.World.pocketMaps.Contains((PocketMapParent)retry.Parent)
                && !RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects, "Failed native unload lost retry ownership or leaked scope.");
            fail = false; PocketMapUtility.DestroyPocketMap(retry);
            Check(!maps.Contains(retry), "Native collapse retry failed.");
            SnapshotTree();
        }
        finally { harmony.UnpatchAll(harmony.Id); Current.Game = previous; maps.Clear(); removed.Clear(); cleaned.Clear(); fail = false; }
    }
    private static void SnapshotTree()
    {
        var world = new XElement("world", new XElement("worldObjects", new XElement("li", new XElement("def", "ClashOfRim_RemoteRaidBattleMapParent"),
            new XElement("ID", 1), new XElement("clashOfRimMode", "RaidBattle"))),
            new XElement("pocketMaps", PocketXml(3, 2), PocketXml(2, 1), PocketXml(5, 4)));
        var document = new XDocument(new XElement("savegame", new XElement("game", world,
            new XElement("maps", Enumerable.Range(1, 5).Select(id => new XElement("li", new XElement("uniqueID", id),
                new XElement("mapInfo", new XElement("parent", "WorldObject_" + id))))))));
        byte[] input = Encoding.UTF8.GetBytes(document.ToString());
        var output = SnapshotSaveSanitizer.RemoveTransientObservationState(input, out var result, true);
        var saved = XDocument.Parse(Encoding.UTF8.GetString(output).TrimStart('\uFEFF'));
        Check(result.RemovedMaps == 3 && result.RemovedWorldObjects == 1
            && saved.Descendants("pocketMaps").Elements("li").Single().Element("ID")!.Value == "5",
            "Save cleanup left nested pockets or removed local pocket state.");
        Check(ReferenceEquals(output, SnapshotSaveSanitizer.RemoveTransientObservationState(output, out _, true)), "Repeated save cleanup changed bytes.");
    }
    private static XElement PocketXml(int id, int source) => new("li", new XElement("ID", id), new XElement("sourceMap", "Map_" + source));
    private static Map Map(MapParent parent, int id) { var map = New<Map>(); map.info = new MapInfo { parent = parent }; map.uniqueID = id; maps.Add(map); return map; }
    private static Map Pocket(Map source, int id) { var parent = new PocketMapParent { sourceMap = source }; Find.World.pocketMaps.Add(parent); return Map(parent, id); }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static HarmonyMethod Method(string name) => new(typeof(RemotePocketMapTests), name);
    private static bool Maps(ref List<Map> __result) { __result = maps; return false; }
    private static bool ParentMap(MapParent __instance, ref Map? __result) { __result = maps.FirstOrDefault(m => m.Parent == __instance); return false; }
    private static bool CurrentMap(ref Map? __result) { __result = null; return false; }
    private static bool Skip() => false;
    private static bool Cleanup(Map map, RemoteSessionMapParent parent, ref int __result)
    { Check(parent == owner, "Cleanup used a different session."); cleaned.Add(map); __result = 0; return false; }
    private static bool Remove(Map map)
    {
        Check(RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects, "Native unload escaped suppression.");
        if (fail) throw new InvalidOperationException("engine unload failure");
        removed.Add(map); maps.Remove(map); return false;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
