using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

// Real closure, session component and facade; substitute only engine unloading/pawn/UI effects.
internal static class ClientRemoteMapCloseRuntimeTests
{
    private static Game game = null!;
    private static World world = null!;
    private static readonly List<WorldObject> parents = new();
    private static readonly List<Map> maps = new();
    private static readonly Dictionary<MapParent, Map?> parentMaps = new();
    private static readonly Dictionary<Map, MapParent> mapParents = new();
    private static readonly List<string> trace = new();
    private static Action<string>? after;
    private static bool failRemove, removeBeforeFailure;
    private static RemoteSessionMapParent parent = null!;
    private static ActiveRaidBattleSession battle = null!;
    private static ActiveRemoteMapSession observation = null!;
    private static Map map = null!;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RemoteMapClose");
        try
        {
            void Patch(Type type, string method, string prefix) => harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(ClientRemoteMapCloseRuntimeTests), prefix));
            void Getter(Type type, string property, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, property), prefix: new HarmonyMethod(typeof(ClientRemoteMapCloseRuntimeTests), prefix));
            Getter(typeof(Current), nameof(Current.Game), nameof(Game));
            Getter(typeof(Game), nameof(Verse.Game.World), nameof(World));
            Getter(typeof(Game), nameof(Verse.Game.Maps), nameof(Maps));
            Getter(typeof(MapParent), nameof(MapParent.Map), nameof(ParentMap));
            Getter(typeof(Map), nameof(Verse.Map.Parent), nameof(MapOwner));
            Getter(typeof(Find), nameof(Find.CurrentMap), nameof(CurrentMap));
            Getter(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.AllWorldObjects), nameof(WorldObjects));
            Patch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Contains), nameof(Contains));
            Patch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove), nameof(RemoveParent));
            Patch(typeof(RaidTrapVisibilityController), nameof(RaidTrapVisibilityController.EndHiddenTrapSession), nameof(Traps));
            Patch(typeof(RemoteSessionPocketMapUtility), nameof(RemoteSessionPocketMapUtility.ClosePocketMapsForSource), nameof(Pockets));
            Patch(typeof(RemoteSessionMapUtility), nameof(RemoteSessionMapUtility.PrepareCurrentMapForRemoval), nameof(Fallback));
            Patch(typeof(RemoteSessionThingCleanup), nameof(RemoteSessionThingCleanup.DiscardMarkedMapThings), nameof(Things));
            Patch(typeof(RemoteSessionThingCleanup), nameof(RemoteSessionThingCleanup.DiscardMarkedReferencePawns), nameof(References));
            Patch(typeof(Game), nameof(Verse.Game.DeinitAndRemoveMap), nameof(RemoveMap));
            Patch(typeof(RemoteMapSnapshotProjector), nameof(RemoteMapSnapshotProjector.CleanupUnusedRemoteNpcFactions), nameof(Factions));
            Patch(typeof(ClientRemoteSessionRuntime), nameof(ClientRemoteSessionRuntime.PauseRaidBattleTicking), nameof(Skip));
            Patch(typeof(ClashLog), nameof(ClashLog.Message), nameof(Skip));
            Patch(typeof(Log), nameof(Log.Warning), nameof(Skip));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientRemoteMapCloseRuntimeTests), nameof(Text)));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Messages)).Where(m => m.Name == nameof(Messages.Message)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientRemoteMapCloseRuntimeTests), nameof(Message)));

            foreach (bool raid in new[] { false, true })
            foreach (bool loaded in new[] { false, true })
            foreach (bool clear in new[] { false, true })
            {
                Reset(raid, loaded);
                var operation = new ClientRemoteMapCloseOperation(parent);
                Check(ClientRemoteMapClosure.Close(operation, "close", clear), "Valid engine close failed.");
                Check(!parents.Contains(parent) && !maps.Contains(map) && trace.Count(s => s == "references") == 1, "Did not unload exactly once.");
                Check((raid ? ClashOfRimGameComponent.ActiveRaidBattleSession is null : ClashOfRimGameComponent.ActiveRemoteMapSession is null) == clear, "Session clear policy changed.");
                Check(!ClientRemoteMapClosure.Close(operation, "duplicate", clear), "Ticket reused.");
                int effects = trace.Count;
                Check(!ClientRemoteMapClosure.Close(new ClientRemoteMapCloseOperation(parent), "duplicate", clear) && trace.Count == effects, "Detached parent closed twice.");
                Check(!RemoteSessionMapUtility.ExplicitRaidBattleMapCloseInProgress, "Explicit removal scope leaked.");
            }
            foreach (bool raid in new[] { false, true })
            foreach (string change in new[] { "game", "world", "session", "parent", "map", "mode", "id" })
            foreach (string phase in new[] { "before", "traps", "pockets", "fallback", "things", "remove", "references" })
            {
                Reset(raid, true);
                var operation = new ClientRemoteMapCloseOperation(parent);
                void Replace()
                {
                    switch (change)
                    {
                        case "game": game = NewGame(); break;
                        case "world": world = NewWorld(); break;
                        case "session": if (raid) ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid", ClientMapId = "Map_7" });
                            else ClashOfRimGameComponent.SetActiveObservationSession(ActiveRemoteMapSession.FromObservation("observation", "Scout", "other", "c", "s")); break;
                        case "parent": parents.Remove(parent); break;
                        case "map": parentMaps[parent] = Uninitialized<Map>(); break;
                        case "mode": parent.Mode = raid ? "Scout" : "RaidBattle"; break;
                        case "id": parent.SessionId = "replacement"; break;
                    }
                }
                if (phase == "before") Replace(); else after = step => { if (step == phase) Replace(); };
                Check(!ClientRemoteMapClosure.Close(operation, "stale", true) && !trace.Contains("factions"), "Stale engine close completed: " + change + "/" + phase);
                Check(!RemoteSessionMapUtility.ExplicitRaidBattleMapCloseInProgress, "Stale removal scope leaked.");
            }
            Reset(false, true);
            after = step => { if (step == "traps") Check(!ClientRemoteMapClosure.Close(new ClientRemoteMapCloseOperation(parent), "nested", true), "Second ticket reentered same map."); };
            Check(ClientRemoteMapClosure.Close(new ClientRemoteMapCloseOperation(parent), "outer", true), "Nested close broke owner.");

            // A retained observation toolbar action must not close its successor, even with identical IDs.
            Reset(false, true); var old = observation;
            ClashOfRimGameComponent.SetActiveObservationSession(ActiveRemoteMapSession.FromObservation(old.SessionId, old.Mode, "new", "c", "s"));
            Check(!RemoteMapSessionController.TryRunPrimaryAction(null, old, "finish") && trace.Count == 0, "Old UI closed current observation.");
            Reset(true, true); var stale = battle;
            ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = stale.EventId, ClientMapId = "Map_7", FinishInProgress = true });
            RemoteMapSessionController.ResetRaidBattleFinish(stale);
            Check(!RemoteMapSessionController.TryBeginRaidBattleFinish(stale)
                && !RemoteMapSessionController.CloseRaidBattleAfterSettlementAccepted(stale, "late")
                && !RemoteMapSessionController.CloseRaidBattleForFinalDeadline(stale)
                && ClashOfRimGameComponent.ActiveRaidBattleSession!.FinishInProgress && trace.Count == 0, "Old battle modified same-ID successor.");
            Check(!RemoteSessionMapUtility.CloseRaidBattleMap(null, "missing") && !RemoteSessionMapUtility.CloseRaidBattleMap(" ", "missing")
                && !RemoteSessionMapUtility.CloseRaidBattleMap("other", "wrong"), "Empty/wrong event selected arbitrary battle.");
            RemoteSessionMapUtility.CloseObservationMap(parent);
            Check(trace.Count == 0, "Observation close bypassed battle settlement.");

            // An orphan observation sharing an event with the new session may be removed without clearing new records.
            Reset(false, true);
            var newer = ActiveRemoteMapSession.FromObservation("new", "RaidObservation", "new", "c", "s", "raid");
            newer.ClientMapId = "Map_8"; ClashOfRimGameComponent.SetActiveObservationSession(newer);
            var records = new List<RemoteMapThingIdentityRecord>
            {
                new() { SessionId = parent.SessionId, RelatedEventId = "raid", MapUniqueId = "7", ProjectedThingId = "1", OriginalThingId = "a" },
                new() { SessionId = "new", RelatedEventId = "raid", MapUniqueId = "8", ProjectedThingId = "2", OriginalThingId = "b" }
            };
            AccessTools.Field(typeof(ClashOfRimGameComponent), "remoteMapThingIdentities").SetValue(game.GetComponent<ClashOfRimGameComponent>(), records);
            RemoteSessionMapUtility.CloseObservationMap(parent);
            Check(ReferenceEquals(newer, ClashOfRimGameComponent.ActiveRemoteMapSession) && records.Count == 1 && records[0].SessionId == "new", "Orphan cleanup removed new shared-event projection/session.");

            foreach (bool removed in new[] { false, true })
            {
                Reset(true, true); failRemove = true; removeBeforeFailure = removed;
                var runtime = new ClientRaidSettlementRuntime(battle); bool threw = false;
                try { runtime.CloseAccepted("accepted"); } catch (InvalidOperationException) { threw = true; }
                Check(threw != removed && runtime.IsCurrent != removed && parents.Contains(parent) != removed, "Failed unload lost active recovery state.");
                Check(!RemoteSessionMapUtility.ExplicitRaidBattleMapCloseInProgress, "Throwing removal scope leaked.");
                if (!removed) { failRemove = false; runtime.CloseAccepted("retry"); Check(!runtime.IsCurrent, "Failed close could not retry."); }
            }
            Reset(true, true); failRemove = true;
            Check(new ClientRaidSettlementRuntime(battle).CloseExpired(true) && parents.Contains(parent)
                && ClashOfRimGameComponent.ActiveRaidBattleSession is not null,
                "Expiry did not retain failed local unload for authoritative reload.");
            // Duplicate event IDs do not replace the session's captured runtime map selection.
            Reset(true, true);
            var duplicate = new RemoteSessionMapParent { SessionId = "other", RelatedEventId = "raid", Mode = "RaidBattle" };
            var duplicateMap = Uninitialized<Map>(); duplicateMap.uniqueID = 8;
            parents.Insert(0, duplicate); maps.Add(duplicateMap); parentMaps[duplicate] = duplicateMap; mapParents[duplicateMap] = duplicate;
            new ClientRaidSettlementRuntime(battle).CloseAccepted("exact map");
            Check(parents.Contains(duplicate) && maps.Contains(duplicateMap) && !parents.Contains(parent), "Event-only close chose another map.");
            // Closing an orphan battle cannot clear the newer same-event battle after its old map disappears.
            Reset(true, true); ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid", ClientMapId = "Map_8" });
            Check(ClientRemoteMapClosure.Close(new ClientRemoteMapCloseOperation(parent), "orphan", true)
                && ClashOfRimGameComponent.ActiveRaidBattleSession?.ClientMapId == "Map_8", "Post-unload lookup cleared another same-event battle.");
            foreach (bool replaceGame in new[] { false, true })
            {
                Reset(true, true); var runtime = new ClientRaidSettlementRuntime(battle);
                after = step => { if (step == "remove") { if (replaceGame) game = NewGame(); ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid", ClientMapId = "Map_8" }); } };
                runtime.CloseAccepted("accepted");
                Check(ClashOfRimGameComponent.ActiveRaidBattleSession?.ClientMapId == "Map_8", "Accepted close cleared successor created by unload callback.");
            }
            Reset(false, true);
            Check(RemoteSessionMapUtility.CloseExistingSessionMaps() && parents.Count == 0, "Observation replacement did not close old maps.");
            Reset(true, true);
            Check(!RemoteSessionMapUtility.CloseExistingSessionMaps() && trace.Count == 0, "Observation replacement bypassed active battle.");
            Reset(false, true); after = step => { if (step == "remove") game = NewGame(); };
            Check(!RemoteSessionMapUtility.CloseExistingSessionMaps(), "Map opening continued after close switched the game.");
            Reset(false, true);
            var ordinary = Uninitialized<MapParent>(); parents.Add(ordinary); parentMaps[ordinary] = map;
            Check(!ClientRemoteMapClosure.Close(new ClientRemoteMapCloseOperation(ordinary), "local", true), "Local map accepted as observation.");
            var legacy = Uninitialized<RemoteColonyMapParent>(); parents.Add(legacy); parentMaps[legacy] = map; mapParents[map] = legacy;
            Check(ClientRemoteMapClosure.Close(new ClientRemoteMapCloseOperation(legacy), "legacy", true)
                && parents.Contains(legacy) && ClashOfRimGameComponent.ActiveRemoteMapSession is not null, "Legacy remote marker or unrelated session removed.");
        }
        catch (Exception exception) { Console.WriteLine(exception.StackTrace); throw; }
        finally { harmony.UnpatchAll(harmony.Id); after = null; parents.Clear(); maps.Clear(); parentMaps.Clear(); mapParents.Clear(); trace.Clear(); }
    }
    private static void Reset(bool raid, bool loaded)
    {
        after = null; failRemove = removeBeforeFailure = false; trace.Clear(); parents.Clear(); maps.Clear(); parentMaps.Clear(); mapParents.Clear();
        game = NewGame(); world = NewWorld();
        parent = new RemoteSessionMapParent { SessionId = "observation", RelatedEventId = "raid", Mode = raid ? "RaidBattle" : "RaidObservation" };
        map = Uninitialized<Map>(); map.uniqueID = 7; parents.Add(parent); parentMaps[parent] = loaded ? map : null; mapParents[map] = parent;
        if (loaded) maps.Add(map);
        battle = new() { EventId = "raid", ClientMapId = "Map_7" };
        observation = ActiveRemoteMapSession.FromObservation("observation", "RaidObservation", "target", "c", "s", "raid"); observation.ClientMapId = "Map_7";
        if (raid) ClashOfRimGameComponent.SetActiveRaidBattleSession(battle); else ClashOfRimGameComponent.SetActiveObservationSession(observation);
    }
    private static Game NewGame()
    {
        var value = Uninitialized<Game>();
        AccessTools.Field(typeof(Game), "components").SetValue(value, new List<GameComponent> { new ClashOfRimGameComponent(value) }); return value;
    }
    private static World NewWorld() { var value = Uninitialized<World>(); value.worldObjects = Uninitialized<WorldObjectsHolder>(); return value; }
    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Step(string name) { trace.Add(name); after?.Invoke(name); }
    private static bool Game(ref Game __result) { __result = game; return false; }
    private static bool World(ref World __result) { __result = world; return false; }
    private static bool Maps(ref List<Map> __result) { __result = maps; return false; }
    private static bool ParentMap(MapParent __instance, ref Map? __result) { __result = parentMaps.GetValueOrDefault(__instance); return false; }
    private static bool MapOwner(Map __instance, ref MapParent __result) { __result = mapParents[__instance]; return false; }
    private static bool CurrentMap(ref Map? __result) { __result = null; return false; }
    private static bool WorldObjects(ref List<WorldObject> __result) { __result = parents; return false; }
    private static bool Contains(WorldObject __0, ref bool __result) { __result = parents.Contains(__0); return false; }
    private static bool RemoveParent(WorldObject __0) { parents.Remove(__0); Step("parent"); return false; }
    private static bool Traps() { Step("traps"); return false; }
    private static bool Pockets() { Step("pockets"); return false; }
    private static bool Fallback() { Step("fallback"); return false; }
    private static bool Things() { Step("things"); return false; }
    private static bool References() { Step("references"); return false; }
    private static bool Factions() { Step("factions"); return false; }
    private static bool Message() { Step("message"); return false; }
    private static bool RemoveMap(Map __0)
    {
        Check(RemoteSessionMapUtility.ExplicitRaidBattleMapCloseInProgress == parent.IsRaidBattle, "Wrong explicit removal scope.");
        if (!failRemove || removeBeforeFailure) { maps.Remove(__0); parentMaps[mapParents[__0]] = null; }
        Step("remove");
        if (failRemove) throw new InvalidOperationException("unload");
        return false;
    }
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private static bool Skip() => false;
}
