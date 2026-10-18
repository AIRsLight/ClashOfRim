using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.Modules.Raids;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

// Exercise the real source and game component, substituting engine/service effects only.
internal static class ClientRaidSupportLossSourceTests
{
    private static Game game = null!;
    private static ActiveRaidBattleSession battle = null!;
    private static readonly List<ActiveSupportPawnAssignment> assignments = new();
    private static readonly Dictionary<string, Pawn> live = new();
    private static readonly Dictionary<Thing, Map?> maps = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<Thing> destroyed = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<Pawn> dead = new(ReferenceEqualityComparer.Instance);
    private static readonly List<Pawn> removed = new();
    private static bool removeSucceeds;
    private static string label = "支援-é";
    private static readonly List<(string? Event, string Reason, bool Clear)> closed = new();
    private static World? recoveryWorld;
    private static List<Map>? recoveryMaps;
    private static RemoteSessionMapParent? recoveryParent;
    private static Map? recoveryMap;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RaidSupportLossSource");
        try
        {
            Patch(harmony, AccessTools.PropertyGetter(typeof(Current), nameof(Current.Game)), nameof(GamePrefix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Dead)), nameof(DeadPrefix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Destroyed)), nameof(DestroyedPrefix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map)), nameof(MapPrefix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.LabelShort)), nameof(LabelPrefix));
            Patch(harmony, AccessTools.Method(typeof(SupportPawnLookup), nameof(SupportPawnLookup.FindActive)), nameof(FindPrefix));
            Patch(harmony, AccessTools.Method(typeof(PawnExchangeLifecycleService), nameof(PawnExchangeLifecycleService.RemoveFromLocalWorld)), nameof(RemovePrefix));

            Reset();
            assignments[2].AutoReturnOnSettlement = false;
            assignments[3].FinishInProgress = true;
            dead.Add(live["5"]); destroyed.Add(live["6"]); live.Remove("7");
            maps[live["8"]] = null; var otherMap = Uninitialized<Map>(); otherMap.uniqueID = 8; maps[live["9"]] = otherMap;
            var source = new ClientRaidSupportLossSource(battle); var losses = source.Capture();
            Check(losses.Select(loss => loss.EventId).SequenceEqual(new[] { "1", "2" }), "Source selected non-returning, pending, missing, dead or other-map pawns.");
            source.InProgress(losses[0], true);
            Check(assignments[0].FinishInProgress, "Did not mark captured assignment.");
            assignments[0].PawnGlobalKey = "changed"; assignments[0].PawnLabel = "changed";
            Check(losses[0].GlobalKey == "global-1" && losses[0].PawnName == label, "Captured loss aliases mutable assignment.");
            removeSucceeds = false;
            Check(!source.RemoveAndComplete(losses[0]) && assignments.Any(a => a.EventId == "1"), "Failed removal completed assignment.");
            removeSucceeds = true; removed.Clear(); var captured = live["1"];
            Check(source.RemoveAndComplete(losses[0]) && removed.Count == 1 && ReferenceEquals(removed[0], captured)
                && assignments.All(a => a.EventId != "1") && !source.RemoveAndComplete(losses[0]), "Removal did not finish exact assignment once.");

            foreach (string invalid in new[] { "game", "battle", "pawn", "assignment", "loss" })
            {
                Reset(); source = new(battle); var loss = source.Capture()[0]; var original = assignments[0];
                source.InProgress(loss, true);
                switch (invalid)
                {
                    case "game": game = Uninitialized<Game>(); break;
                    case "battle": ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid", ClientMapId = "Map_7" }); break;
                    case "pawn": live["1"] = Uninitialized<Pawn>(); break;
                    case "assignment": assignments[0] = new() { EventId = "1", FinishInProgress = true }; break;
                    case "loss": loss = loss with { }; break;
                }
                Check(!source.RemoveAndComplete(loss) && removed.Count == 0 && assignments.Count == 9, "Stale source removed replacement: " + invalid);
                source.InProgress(loss, false);
                if (invalid != "pawn") Check(original.FinishInProgress && assignments[0].FinishInProgress, "Stale source cleared unrelated flag: " + invalid);
            }
            foreach (bool alreadyDestroyed in new[] { false, true })
            {
                Reset(); label = ""; source = new(battle); var loss = source.Capture()[0]; var pawn = live["1"];
                Check(loss.PawnName == "fallback-1", "Missing pawn label lost assignment fallback.");
                if (alreadyDestroyed) destroyed.Add(pawn); else live.Remove("1");
                Check(source.RemoveAndComplete(loss) && removed.Count == (alreadyDestroyed ? 0 : 1)
                    && assignments.Count == 8, "Captured pawn disappearance prevented correct completion.");
            }
            Reset(); source = new(battle); game = Uninitialized<Game>();
            bool rejected = false;
            try { source.Capture(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Obsolete battle silently became an empty successful batch.");
            SettlementRuntime(harmony);
            RecoveryRuntime(harmony);
        }
        catch (Exception ex) { Console.WriteLine(ex.StackTrace); throw; }
        finally { harmony.UnpatchAll(harmony.Id); Clear(); }
    }
    private static void SettlementRuntime(Harmony harmony)
    {
        Patch(harmony, AccessTools.Method(typeof(RemoteMapSessionController), nameof(RemoteMapSessionController.PauseRaidBattleTicking)), nameof(SkipEffect));
        Patch(harmony, AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), nameof(SkipEffect));
        Patch(harmony, AccessTools.Method(typeof(RemoteSessionMapUtility), nameof(RemoteSessionMapUtility.CloseRaidBattleMap)), nameof(ClosePrefix));
        Reset(); battle.AttackerUserId = "alice"; battle.AttackerColonyId = "colony"; battle.TargetMapId = "42";
        var runtime = new ClientRaidSettlementRuntime(battle);
        battle.TargetMapId = "changed";
        Check(runtime.Identity.TargetMapId == "42" && runtime.Identity.AttackerUserId == "alice" && runtime.IsCurrent
            && runtime.TryBegin() && battle.FinishInProgress && !runtime.TryBegin(), "Runtime did not capture identity or mark finish once.");
        runtime.ResetFinish(); Check(!battle.FinishInProgress, "Runtime failed to reset original finish.");
        runtime.KeepFinish(); Check(battle.FinishInProgress, "Runtime failed to preserve finish.");
        runtime.CloseAccepted("retreat");
        Check(!runtime.IsCurrent && ClashOfRimGameComponent.ActiveRaidBattleSession is null
            && closed.Single() == ("raid", "raid settlement accepted: retreat", false), "Accepted close changed event/reason/clear ordering.");
        runtime.CloseAccepted("duplicate"); Check(closed.Count == 1, "Accepted closure repeated.");
        foreach (bool closeMap in new[] { false, true })
        {
            Reset(); runtime = new(battle); runtime.CloseExpired(closeMap); runtime.CloseExpired(closeMap);
            Check(!runtime.IsCurrent && closed.Count == (closeMap ? 1 : 0), "Expired closure repeated or ignored map policy.");
        }
        foreach (bool replaceGame in new[] { false, true })
        {
            Reset(); runtime = new(battle);
            if (replaceGame) game = Uninitialized<Game>();
            else ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid", FinishInProgress = true });
            runtime.ResetFinish(); runtime.KeepFinish(); runtime.CloseAccepted("late"); runtime.CloseExpired(true);
            Check(!runtime.IsCurrent && !runtime.TryBegin() && closed.Count == 0, "Runtime affected replacement game/battle.");
            if (!replaceGame) Check(ClashOfRimGameComponent.ActiveRaidBattleSession!.FinishInProgress, "Old runtime reset replacement battle flag.");
        }
        Check(!new ClientRaidSettlementRuntime(null).TryBegin(), "Missing battle became active.");
    }
    private static void RecoveryRuntime(Harmony harmony)
    {
        Patch(harmony, AccessTools.PropertyGetter(typeof(Game), nameof(Game.World)), nameof(WorldPrefix));
        Patch(harmony, AccessTools.PropertyGetter(typeof(Game), nameof(Game.Maps)), nameof(MapsPrefix));
        Patch(harmony, AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.Map)), nameof(RecoveryMapPrefix));
        Patch(harmony, AccessTools.Method(typeof(RemoteSessionMapUtility), nameof(RemoteSessionMapUtility.FindActiveSessionMap)), nameof(FindRecoveryMap));
        Reset(); var runtime = new ClientRaidRecoveryRuntime();
        Check(!runtime.Ready && runtime.ReadBattle() is null, "Missing world became ready.");
        recoveryWorld = Uninitialized<World>(); recoveryMaps = new();
        Check(!runtime.Ready, "Missing world objects became ready.");
        recoveryWorld.worldObjects = Uninitialized<WorldObjectsHolder>(); recoveryMaps = null;
        Check(!runtime.Ready, "Missing maps became ready.");
        recoveryMaps = new(); var restored = runtime.ReadBattle();
        Check(runtime.Ready && restored is not null && !restored.HasMap, "Ready game lost captured battle or invented map.");
        recoveryParent = Uninitialized<RemoteSessionMapParent>();
        Check(!restored!.HasMap, "Orphan parent without loaded map counted as recovered battle.");
        recoveryMap = Uninitialized<Map>(); Check(restored.HasMap, "Loaded battle map not found.");
        battle.FinishInProgress = true; restored.RestoreDeadlines(new(10, 20, 30));
        Check(!battle.FinishInProgress && battle.StartedAtUtcTicks == 10 && battle.DeadlineUtcTicks == 20 && battle.FinalDeadlineUtcTicks == 30
            && ReferenceEquals(ClashOfRimGameComponent.ActiveRaidBattleSession, battle), "Recovery lost server clocks, finish reset or battle identity.");
        var replacement = new ActiveRaidBattleSession { EventId = "raid", DeadlineUtcTicks = 42, FinishInProgress = true };
        ClashOfRimGameComponent.SetActiveRaidBattleSession(replacement); restored.RestoreDeadlines(new(1, 2, 3));
        Check(!restored.HasMap && replacement.DeadlineUtcTicks == 42 && replacement.FinishInProgress, "Recovery changed replacement battle.");
        game = Uninitialized<Game>(); Check(!runtime.Ready && runtime.ReadBattle() is null, "Recovery runtime rebound to another game.");
    }
    private static void Reset()
    {
        Clear(); removeSucceeds = true; label = "支援-é";
        game = Uninitialized<Game>(); var component = new ClashOfRimGameComponent(game);
        AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { component });
        AccessTools.Field(typeof(ClashOfRimGameComponent), "activeSupportAssignments").SetValue(component, assignments);
        battle = new() { EventId = "raid", ClientMapId = "Map_7" }; ClashOfRimGameComponent.SetActiveRaidBattleSession(battle);
        var map = Uninitialized<Map>(); map.uniqueID = 7;
        for (int i = 1; i <= 9; i++)
        {
            string id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var pawn = Uninitialized<Pawn>(); live.Add(id, pawn); maps.Add(pawn, map);
            assignments.Add(new() { EventId = id, PawnThingId = id, PawnGlobalKey = "global-" + id, PawnLabel = "fallback-" + id, AutoReturnOnSettlement = true });
        }
    }
    private static void Clear()
    {
        assignments.Clear(); live.Clear(); maps.Clear(); destroyed.Clear(); dead.Clear(); removed.Clear(); closed.Clear();
        recoveryWorld = null; recoveryMaps = null; recoveryParent = null; recoveryMap = null;
    }
    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Patch(Harmony harmony, MethodInfo method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientRaidSupportLossSourceTests), prefix));
    private static bool GamePrefix(ref Game __result) { __result = game; return false; }
    private static bool DeadPrefix(Pawn __instance, ref bool __result) { __result = dead.Contains(__instance); return false; }
    private static bool DestroyedPrefix(Thing __instance, ref bool __result) { __result = destroyed.Contains(__instance); return false; }
    private static bool MapPrefix(Thing __instance, ref Map? __result) { __result = maps.GetValueOrDefault(__instance); return false; }
    private static bool LabelPrefix(ref string __result) { __result = label; return false; }
    private static bool FindPrefix(string __0, ref Pawn? __result) { __result = live.GetValueOrDefault(__0); return false; }
    private static bool RemovePrefix(Pawn __0, ref bool __result) { removed.Add(__0); __result = removeSucceeds; return false; }
    private static bool SkipEffect() => false;
    private static bool WorldPrefix(ref World? __result) { __result = recoveryWorld; return false; }
    private static bool MapsPrefix(ref List<Map>? __result) { __result = recoveryMaps; return false; }
    private static bool RecoveryMapPrefix(ref Map? __result) { __result = recoveryMap; return false; }
    private static bool FindRecoveryMap(ActiveRemoteMapSession? __0, ref RemoteSessionMapParent? __result)
    { Check(__0?.RelatedEventId == "raid", "Recovery looked up another event."); __result = recoveryParent; return false; }
    private static bool ClosePrefix(string? __0, string __1, bool __2, ref bool __result)
    { closed.Add((__0, __1, __2)); __result = true; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
