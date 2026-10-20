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
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class RemotePocketMapTests
{
    private static readonly List<Map> maps = new();
    private static readonly List<Map> removed = new();
    private static readonly List<Map> cleaned = new();
    private static readonly List<Pawn> references = new();
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
            var nativeExit = New<CaveExit>(); var unsupported = New<MapPortal>();
            foreach (var portal in new MapPortal[] { nativeExit, unsupported })
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(portal, (sbyte)maps.IndexOf(child));
            Check(RemotePocketMapAdmission.Allows(nativeExit) && !RemotePocketMapAdmission.Allows(unsupported),
                "Generation admission blocked an existing native return path or allowed an unknown entrance.");
            var point = new Thing { def = new ThingDef { defName = "DefensePoint" }, thingIDNumber = 15 };
            RaidTrapVisibilityController.ApplyHiddenThingSession("raid", "snapshot", root, new[] { point });
            int hidden = RaidTrapVisibilityController.HiddenCount;
            RaidTrapVisibilityController.ApplyHiddenThingSession("raid", "snapshot", child, Array.Empty<Thing>());
            Check(hidden > 0 && RaidTrapVisibilityController.HiddenCount == hidden
                && RaidTrapVisibilityController.CurrentTargetMapLoadId == root.GetUniqueLoadID(),
                "Child map postprocessing erased root defense-point concealment.");
            RaidTrapVisibilityController.EndHiddenTrapSession();
            var pawn = New<Pawn>(); pawn.def = new ThingDef { defName = "TestPawn" }; pawn.thingIDNumber = 17;
            RemoteSessionPocketMapPawnPatch.Postfix(pawn, nested);
            Check(RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(pawn), "New child pawn missed cleanup identity.");
            pawn.questTags = null;
            ClashOfRimGameComponent.SetActiveRaidBattleSession(new ActiveRaidBattleSession { EventId = "battle", AttackPawnThingIds = new List<string> { pawn.ThingID } });
            RemoteSessionPocketMapPawnPatch.Postfix(pawn, nested);
            Check(!RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(pawn), "Owned attacker became disposable after native portal entry.");
            ClashOfRimGameComponent.RegisterRemoteMapThingIdentities(owner, "1", new[] { new RemoteMapProjectedThingIdentity("Thing11", "Thing1") }, "10");
            ClashOfRimGameComponent.RegisterRemoteMapThingIdentities(owner, "2", new[] { new RemoteMapProjectedThingIdentity("Thing12", "Thing2") }, "20");
            ClashOfRimGameComponent.RegisterRemoteMapThingIdentities(owner, "3", new[] { new RemoteMapProjectedThingIdentity("Thing13", "Thing3") }, "30");
            Check(ClashOfRimGameComponent.CopyRemoteMapThingIdentities().Select(i => i.SourceMapId).SequenceEqual(new[] { "10", "20", "30" }),
                "Child identities were assigned to the root source map or replaced another map's identities.");
            RemoteSessionPocketMapUtility.ClosePocketMapsForSource(root, "test");
            Check(removed.SequenceEqual(new[] { nested, child }) && cleaned.SequenceEqual(removed)
                && maps.SequenceEqual(new[] { root, home, localPocket }) && Find.World.pocketMaps.Single() == localPocket.Parent,
                "Native close failed child-first ordering or removed local maps.");
            Check(ClashOfRimGameComponent.CopyRemoteMapThingIdentities().Single().MapUniqueId == "1", "Pocket close retained stale identities or removed root identities.");
            RemoteSessionPocketMapUtility.ClosePocketMapsForSource(root, "repeat");
            Check(removed.Count == 2 && !RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects, "Close repeated effects or leaked scope.");
            var retry = Pocket(root, 6); fail = true;
            try { PocketMapUtility.DestroyPocketMap(retry); throw new Exception("Unload should fail."); }
            catch (InvalidOperationException) { }
            Check(maps.Contains(retry) && Find.World.pocketMaps.Contains((PocketMapParent)retry.Parent)
                && !RemoteSessionGlobalStateGuard.SuppressRemoteMapRemovalGlobalEffects, "Failed native unload lost retry ownership or leaked scope.");
            fail = false; PocketMapUtility.DestroyPocketMap(retry);
            Check(!maps.Contains(retry), "Native collapse retry failed.");
            ReferencePawnLifetime(harmony);
            SnapshotTree();
            ProjectionTree();
            DeferredTree(harmony);
        }
        finally { harmony.UnpatchAll(harmony.Id); Current.Game = previous; maps.Clear(); removed.Clear(); cleaned.Clear(); references.Clear(); fail = false; }
    }
    private static void ReferencePawnLifetime(Harmony harmony)
    {
        Find.World.worldPawns = New<WorldPawns>();
        harmony.Patch(AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.Contains)), prefix: Method(nameof(ContainsReference)));
        harmony.Patch(AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.PassToWorld)), prefix: Method(nameof(KeepReference)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(WorldPawns), nameof(WorldPawns.AllPawnsAliveOrDead)), prefix: Method(nameof(ReferencePawns)));
        harmony.Patch(AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.RemoveAndDiscardPawnViaGC)), prefix: Method(nameof(RemoveReference)));
        harmony.Patch(AccessTools.Method(typeof(AIRsLight.ClashOfRim.Pawns.PawnExchangeLifecycleService), nameof(AIRsLight.ClashOfRim.Pawns.PawnExchangeLifecycleService.CleanupRelationshipPlaceholdersAfterRemoval)), prefix: Method(nameof(NoRelationships)));
        var dead = New<Pawn>(); var discarded = New<Pawn>();
        dead.def = discarded.def = new ThingDef { defName = "ReferencePawn" };
        dead.thingIDNumber = 31; discarded.thingIDNumber = 32;
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(dead, (sbyte)-2);
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(discarded, (sbyte)-3);
        Check(dead.Destroyed && !dead.Discarded, "Fixture does not represent native dead-pawn storage.");
        Check(RemoteSessionThingCleanup.KeepReferencePawnsForCleanup(new[] { dead, discarded, dead }, owner) == 1
            && references.Single() == dead && RemoteSessionThingCleanup.HasRemoteSessionCleanupTag(dead),
            "Dead corpse reference was not retained for save, or an already discarded pawn was retained.");
        RemoteSessionThingCleanup.KeepReferencePawnsForCleanup(new[] { dead }, owner);
        Check(references.Count == 1, "Reference retention registered the same pawn twice.");
        Check(RemoteSessionThingCleanup.DiscardMarkedReferencePawns(owner, "test") == 1 && references.Count == 0,
            "Session close leaked a retained dead reference pawn.");
    }
    private static bool ContainsReference(Pawn p, ref bool __result) { __result = references.Contains(p); return false; }
    private static bool KeepReference(Pawn pawn, PawnDiscardDecideMode discardMode)
    { Check(discardMode == PawnDiscardDecideMode.KeepForever, "Reference pawn was not protected from GC."); references.Add(pawn); return false; }
    private static bool ReferencePawns(ref IEnumerable<Pawn> __result) { __result = references; return false; }
    private static bool RemoveReference(Pawn p) { references.Remove(p); return false; }
    private static bool NoRelationships(ref int __result) { __result = 0; return false; }
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
    private static void DeferredTree(Harmony harmony)
    {
        var source = XDocument.Parse(@"
            <savegame><game><world><pocketMaps><li><ID>20</ID><sourceMap>Map_1</sourceMap></li><li><ID>30</ID><sourceMap>Map_2</sourceMap></li></pocketMaps></world><maps>
            <li><uniqueID>1</uniqueID><mapInfo><parent>WorldObject_10</parent></mapInfo><things><thing><id>Gate1</id><pocketMap>Map_2</pocketMap><exit>Thing_Exit2</exit></thing></things></li>
            <li><uniqueID>2</uniqueID><mapInfo><parent>WorldObject_20</parent></mapInfo><things><thing><id>Exit2</id><pitGate>Thing_Gate1</pitGate></thing><thing><id>Gate2</id><pocketMap>Map_3</pocketMap><exit>Thing_Exit3</exit></thing></things></li>
            <li><uniqueID>3</uniqueID><mapInfo><parent>WorldObject_30</parent></mapInfo><things><thing><id>Exit3</id><pitGate>Thing_Gate2</pitGate></thing></things></li>
            </maps></game></savegame>
            ");
        int id = 0, parentId = 30;
        var tree = new RemoteMapProjectionTree(source, source.Descendants("maps").Elements().First(), "WorldObject_100", () => ++id, () => ++parentId);
        int before = maps.Count;
        owner.DeferredPockets.Prepare(tree, new AIRsLight.ClashOfRim.ClientNetwork.ModSnapshotPackageMetadataDto { SnapshotId = "snapshot" }, owner);
        Check(owner.DeferredPockets.Count == 2 && maps.Count == before && tree.Maps.Elements().Count() == 1 && !tree.PocketParents.HasElements
            && tree.Maps.Descendants("pocketMap").Single().Value == "null" && tree.Maps.Descendants("exit").Single().Value == "null",
            "Deferring a tree instantiated pockets or retained unresolved forward portal links.");
        var pending = (XElement)AccessTools.Field(typeof(RemoteMapDeferredPockets), "pockets").GetValue(owner.DeferredPockets)!;
        Check(pending.Elements().First().Descendants("pocketMap").Single().Value == "null"
            && pending.Elements().First().Descendants("pitGate").Single().Value == "Thing_Gate1",
            "Staging one child retained a forward link to its unvisited child or erased its exit back to the surface.");
        var portal = New<MapPortal>(); portal.def = new ThingDef { defName = "Gate" }; portal.thingIDNumber = 1;
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(portal, (sbyte)0);
        Check(owner.DeferredPockets.Contains(portal) && RemotePocketMapAdmission.Allows(portal),
            "Existing saved content was subjected to the unknown-generator restriction.");
        var load = AccessTools.Method(typeof(RemoteMapRuntimeLoader), "TryLoad");
        var postprocess = AccessTools.Method(typeof(RemoteObservationMapPostProcessor), "Apply");
        harmony.Patch(load, prefix: Method(nameof(LoadDeferredFixture)));
        harmony.Patch(postprocess, prefix: Method(nameof(FailDeferredPostprocess)));
        harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), prefix: Method(nameof(Skip)));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Map? result = null;
            Check(!RemotePocketMapGenerationAdmissionPatch.Prefix(portal, ref result) && result is null
                && owner.DeferredPockets.Count == 2 && maps.Count == before
                && !owner.DeferredPockets.Retains(removed.Last()),
                "Failed import fell back to generation, lost its retry snapshot, leaked a map or cached disposed objects for retry.");
        }
        harmony.Unpatch(load, HarmonyPatchType.Prefix, harmony.Id);
        harmony.Unpatch(postprocess, HarmonyPatchType.Prefix, harmony.Id);
        // A successful parent-map load consumes its entry; later collapse also drops its unvisited child.
        pending.Elements().First().Remove();
        owner.DeferredPockets.NotifyMapRemoved("Map_2");
        Check(owner.DeferredPockets.Count == 0, "Collapsing a loaded map retained pending descendants.");
        owner.DeferredPockets.Prepare(new RemoteMapProjectionTree(source, source.Descendants("maps").Elements().First(),
            "WorldObject_100", () => ++id, () => ++parentId), new AIRsLight.ClashOfRim.ClientNetwork.ModSnapshotPackageMetadataDto(), owner);
        owner.DeferredPockets.ForgetDescendants("Map_4");
        Check(owner.DeferredPockets.Count == 0, "Dropping an unvisited tree left nested snapshots behind.");
    }
    private static bool LoadDeferredFixture(ref Map? map, ref string failureReason,
        Action<IEnumerable<ILoadReferenceable>> rememberReferences, ref bool __result)
    {
        map = Pocket(maps[0], 2); failureReason = "";
        rememberReferences(new ILoadReferenceable[] { map }); __result = true; return false;
    }
    private static bool FailDeferredPostprocess() => throw new InvalidOperationException("postprocess fault");
    private static void ProjectionTree()
    {
        XElement M(int id, int parent) => new("li", new XElement("uniqueID", id), new XElement("mapInfo", new XElement("parent", "WorldObject_" + parent)));
        var root = M(1, 10); var child = M(2, 20); var nested = M(3, 30);
        root.Add(new XElement("portal", new XElement("pocketMap", "Map_2"), new XElement("exit", "Thing_Exit3")));
        nested.Add(new XElement("portal", new XElement("pitGate", "Thing_Gate1"), new XElement("map", "@Map_2")));
        var source = new XDocument(new XElement("savegame", new XElement("game", new XElement("maps", root, child, nested, M(4, 40)),
            new XElement("world", new XElement("pocketMaps", PocketXml(30, 2), PocketXml(20, 1), PocketXml(40, 9))))));
        root = source.Descendants("maps").Elements().First();
        string original = source.ToString(); int mapId = 1, parentId = 29;
        var tree = new RemoteMapProjectionTree(source, root, "WorldObject_20", () => ++mapId, () => ++parentId);
        Check(source.ToString() == original && tree.Maps.Elements().Count() == 3 && tree.PocketParents.Elements().Count() == 2,
            "Tree projection mutated its source or included unrelated maps.");
        Check(tree.Maps.Elements().First().Element("mapInfo")!.Element("parent")!.Value == "WorldObject_20"
            && tree.Maps.Descendants("pocketMap").Single().Value == "Map_3"
            && tree.Maps.Descendants("map").Single().Value == "@Map_3"
            && tree.PocketParents.Elements().Select(p => p.Element("sourceMap")!.Value).OrderBy(v => v).SequenceEqual(new[] { "Map_2", "Map_3" }),
            "Overlapping source/local IDs broke cross-map links.");
        var signals = XElement.Parse("<things><questTags><li>Quest9</li></questTags><hackingCompletedSignal>Quest9.Hacked</hackingCompletedSignal><threatSignal>RoomThreat3</threatSignal><signalTag>RoomThreat3</signalTag><quest>Quest_9</quest><questDef>AncientStockpile</questDef><sendSignal>true</sendSignal></things>");
        RemoteMapQuestIsolation.Sanitize(signals, "session");
        Check(signals.Element("questTags")!.Element("li")!.Value + ".Hacked" == signals.Element("hackingCompletedSignal")!.Value
            && signals.Element("threatSignal")!.Value == signals.Element("signalTag")!.Value
            && signals.Element("quest")!.Value == "null" && signals.Element("questDef")!.Value == "AncientStockpile"
            && signals.Element("sendSignal")!.Value == "true", "Quest isolation broke local signals or retained a foreign quest reference.");
        Current.Game.uniqueIDsManager = new UniqueIDsManager();
        var layout = XElement.Parse("<maps><li><layoutStructureSketches><li><uniqueId>LayoutStructureSketch_0</uniqueId><structureLayout><rooms><li><id>7</id><loadId>0</loadId><sketch>LayoutStructureSketch_0</sketch><merged><li>LayoutRoom_0</li></merged></li></rooms></structureLayout></li></layoutStructureSketches></li></maps>");
        RemoteMapProjectedLoadIdRewriter.Rewrite(layout, new XElement("references"));
        string sketchId = layout.Descendants("uniqueId").Single().Value;
        string roomId = layout.Descendants("loadId").Single().Value;
        Check(sketchId != "LayoutStructureSketch_0" && roomId != "0"
            && layout.Descendants("sketch").Single().Value == sketchId
            && layout.Descendants("merged").Elements().Single().Value == "LayoutRoom_" + roomId
            && layout.Descendants("id").Single().Value == "7"
            && Find.UniqueIDsManager.GetNextRoomID() > int.Parse(roomId), "Layout remapping broke local room indices, references or native ID allocation.");
        var manyReferences = new XElement("maps", Enumerable.Range(0, 300).Select(i => new XElement("corpsePawn", "Thing_Human" + i)));
        var referenceSource = new XDocument(new XElement("savegame", new XElement("game", new XElement("world",
            new XElement("worldPawns", new XElement("pawnsDead", Enumerable.Range(0, 300).Select(i => new XElement("li",
                new XElement("id", "Human" + i), new XElement("kindDef", "Drifter"), new XElement("pather"), new XElement("jobs")))))))));
        var referencePawns = (XElement)AccessTools.Method(typeof(RemoteMapSnapshotProjector), "BuildReferencePawnsElement")
            .Invoke(null, new object[] { referenceSource, manyReferences })!;
        Check(referencePawns.Elements().Count() == 300, "World-pawn expansion budget truncated mandatory corpse references across the map tree.");
    }
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
