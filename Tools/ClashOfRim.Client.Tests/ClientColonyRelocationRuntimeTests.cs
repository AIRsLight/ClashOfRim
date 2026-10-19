using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class ClientColonyRelocationRuntimeTests
{
    private static readonly List<WorldObject> objects = new();
    private static readonly List<Map> maps = new();
    private static readonly Dictionary<MapParent, Map> parentMaps = new();
    private static readonly Dictionary<Map, MapParent> mapParents = new();
    private static readonly HashSet<Map> homes = new();
    private static Faction player = null!;
    private static int colonists, vanillaCalls;
    private static string? vanillaFailure;
    private static Map? engineMap;
    private static bool guards, remoteThing, failMap;
    private static int warnings;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld; var binding = ClientColonyRelocationCommands.Current;
        var harmony = new Harmony("ClashOfRim.Tests.ColonyRelocationRuntime");
        try
        {
            void Getter(Type type, string property, string prefix) => harmony.Patch(AccessTools.PropertyGetter(type, property), prefix: new HarmonyMethod(typeof(ClientColonyRelocationRuntimeTests), prefix));
            Getter(typeof(Game), nameof(Game.Maps), nameof(Maps)); Getter(typeof(Faction), nameof(Faction.OfPlayer), nameof(Player));
            Getter(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.AllWorldObjects), nameof(Objects));
            Getter(typeof(MapParent), nameof(MapParent.Map), nameof(ParentMap)); Getter(typeof(MapParent), nameof(MapParent.HasMap), nameof(HasMap));
            Getter(typeof(Map), nameof(Map.Parent), nameof(Parent)); Getter(typeof(Map), nameof(Map.IsPlayerHome), nameof(Home));
            Getter(typeof(MapPawns), nameof(MapPawns.FreeColonistsCount), nameof(Colonists));
            Getter(typeof(Caravan), nameof(Caravan.IsPlayerControlled), nameof(Controlled));
            Getter(typeof(ClientColonyRelocationTarget), nameof(ClientColonyRelocationTarget.HasRemoteSession), nameof(NoRemote));
            Getter(typeof(OdysseyCompatibilityPackage), nameof(OdysseyCompatibilityPackage.HasRemoteStateGuards), nameof(Guards));
            Getter(typeof(Thing), nameof(Thing.Map), nameof(EngineMap));
            harmony.Patch(AccessTools.Method(typeof(RemoteSessionGlobalStateGuard), nameof(RemoteSessionGlobalStateGuard.IsRemoteThing)),
                prefix: new HarmonyMethod(typeof(ClientColonyRelocationRuntimeTests), nameof(Remote)));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Log)).Where(m => m.Name == nameof(Log.Warning)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientColonyRelocationRuntimeTests), nameof(Warning)));
            harmony.Patch(AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), prefix: new HarmonyMethod(typeof(ClientColonyRelocationRuntimeTests), nameof(Skip)));
            harmony.Patch(AccessTools.Method(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.SettleCommand)),
                prefix: new HarmonyMethod(typeof(ClientColonyRelocationRuntimeTests), nameof(Vanilla)), postfix: new HarmonyMethod(typeof(ClientColonyRelocationRuntimeTests), nameof(Replacement)));
            harmony.Patch(AccessTools.Method(typeof(Translator), nameof(Translator.Translate), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(ClientColonyRelocationRuntimeTests), nameof(Translate)));
            Current.CreatingWorld = null; var live = New<Game>(); var world = New<World>(); live.World = world; Current.Game = live;
            player = New<Faction>(); player.def = new FactionDef { isPlayer = true };
            world.worldObjects = New<WorldObjectsHolder>(); world.grid = New<WorldGrid>();
            var surface = New<SurfaceLayer>(); Set(surface, "layerId", 0); var underground = New<SurfaceLayer>(); Set(underground, "layerId", 7);
            Set(world.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = surface, [7] = underground }); Set(world.grid, "surface", surface);
            var old = Add(1, new PlanetTile(2, 0), home: true); var destination = Add(2, new PlanetTile(12, 7), home: false);
            var target = new ClientColonyRelocationTarget(destination, parentMaps[destination]); colonists = 1;
            Check(target.IsAvailable && target.HomeCount == 1 && target.Tile == 12 && target.Layer == 7 && target.SettleFailure is null, "Existing map capture lost ownership/layer.");
            colonists = 0; Check(target.SettleFailure == "CommandSettleFailNoColonists", "Base limit masked the missing-colonist check."); colonists = 1;
            Set(destination, "tile", new PlanetTile(12, 0)); Check(!target.IsAvailable, "Same tile on another layer adopted old confirmation."); Set(destination, "tile", new PlanetTile(12, 7));
            maps.Remove(parentMaps[destination]); Check(!target.IsAvailable, "Unloaded destination remained available."); maps.Add(parentMaps[destination]);
            objects.Remove(old); Check(!target.IsAvailable, "Removed original colony remained available."); objects.Add(old);
            Set(destination, "destroyed", true); Check(!target.IsAvailable, "Destroyed destination remained available."); Set(destination, "destroyed", false);
            Check(!target.IsSingleDestination, "Old base was treated as the destination.");
            homes.Remove(parentMaps[old]); homes.Add(parentMaps[destination]);
            Check(target.IsSingleDestination, "Layered destination anchor missing.");
            var landing = new ClientColonyRelocationTarget(destination, parentMaps[destination], implicitMove: true);
            Check(landing.IsAvailable && landing.IsSingleDestination, "Owned landed map rejected.");
            homes.Add(parentMaps[old]); Check(!landing.IsSingleDestination, "Multiple anchors accepted."); homes.Remove(parentMaps[old]);
            var caravan = New<Caravan>(); Set(caravan, "factionInt", player); Set(caravan, "tile", new PlanetTile(15, 7)); objects.Add(caravan);
            var caravanTarget = new ClientColonyRelocationTarget(caravan); vanillaCalls = 0;
            foreach (string? failure in new[] { null, "CommandSettleFailAlreadyHaveBase", "CommandSettleFailReachedMaximumNumberOfBases", "terrain" })
            {
                vanillaFailure = failure;
                Check(caravanTarget.SettleFailure == (failure == "terrain" ? "terrain" : null), "Vanilla rejection conversion changed.");
            }
            Check(vanillaCalls == 4, "Reading vanilla command recursed through replacement.");
            var silentDisabled = new Command_Action(); silentDisabled.Disable(null!);
            Check(ClientColonyRelocationTarget.NonLimitFailure(silentDisabled) is not null, "Missing reason enabled a disabled vanilla command.");
            Entrypoints(old, destination, caravan);
            live.World = New<World>(); Check(!landing.IsCurrent && !caravanTarget.IsCurrent && !target.IsAvailable, "World replacement retained captured targets.");
            live.World = world; Current.Game = New<Game>(); Current.Game.World = world;
            Check(!landing.IsCurrent && !target.IsSingleDestination, "Replacement game adopted old targets.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating;
            ClientColonyRelocationCommands.Attach(binding); engineMap = null; failMap = remoteThing = guards = false;
            objects.Clear(); maps.Clear(); homes.Clear(); parentMaps.Clear(); mapParents.Clear();
        }
    }
    private static void Entrypoints(Settlement old, Settlement destination, Caravan caravan)
    {
        homes.Clear(); homes.Add(parentMaps[old]); vanillaFailure = null;
        var vanilla = new Command_Action { Order = 42f };
        ClientColonyRelocationCommands.Attach(null);
        Command mapCommand = vanilla, caravanCommand = vanilla;
        ColonyRelocationExistingMapSettleCommandPatch.Postfix(parentMaps[destination], false, ref mapCommand);
        ColonyRelocationSettleCommandPatch.Postfix(caravan, ref caravanCommand);
        Check(ReferenceEquals(mapCommand, vanilla) && ReferenceEquals(caravanCommand, vanilla), "Unbound relocation changed vanilla commands.");
        using (var f = new ClientColonyRelocationTests.Fixture())
        {
            var adapter = new ClientColonyRelocationCommands(f.Flow); ClientColonyRelocationCommands.Attach(adapter);
            ColonyRelocationExistingMapSettleCommandPatch.Postfix(parentMaps[destination], false, ref mapCommand);
            ColonyRelocationSettleCommandPatch.Postfix(caravan, ref caravanCommand);
            Check(!ReferenceEquals(mapCommand, vanilla) && !ReferenceEquals(caravanCommand, vanilla)
                && mapCommand.Order == vanilla.Order && caravanCommand.icon == vanilla.icon && !mapCommand.Disabled && !caravanCommand.Disabled,
                "Relocation patches did not use the existing command module.");
            ((Command_Action)mapCommand).action(); Check(f.ConfirmAction is not null, "Map relocation did not open confirmation."); f.ConfirmAction = null;
            ((Command_Action)caravanCommand).action(); Check(f.ConfirmAction is not null, "Caravan relocation did not open confirmation."); f.ConfirmAction = null;
            f.Change("user"); ((Command_Action)mapCommand).action(); ((Command_Action)caravanCommand).action();
            Check(f.ConfirmAction is null, "Old relocation gizmo crossed account ownership.");
            mapCommand = adapter.Build(parentMaps[destination], vanilla, false)!;
            ClientColonyRelocationCommands.Attach(new(f.Flow)); ((Command_Action)mapCommand).action();
            Check(f.ConfirmAction is null, "Old relocation gizmo crossed adapter replacement.");
            Check(adapter.Build(parentMaps[old], vanilla, false) is null, "Home map received relocation command.");
            maps.Remove(parentMaps[destination]); Check(adapter.Build(parentMaps[destination], vanilla, false) is null, "Unloaded map received relocation command."); maps.Add(parentMaps[destination]);
            vanillaFailure = "terrain";
            Check(adapter.Build(caravan, vanilla)!.disabledReason == "terrain", "Relocation hid vanilla terrain restriction."); vanillaFailure = null;
            colonists = 0; Check(adapter.Build(parentMaps[destination], vanilla, true)!.Disabled, "Relocation hid colonist restriction."); colonists = 1;
        }
        var engine = New<Building_GravEngine>(); guards = true; remoteThing = false; engineMap = parentMaps[old]; warnings = 0;
        ClientColonyRelocationCommands.Attach(null);
        Check(RemoteSessionOdysseyGravshipTakeoffPatch.Prefix(engine), "Unbound module blocked ordinary takeoff.");
        using (var f = new ClientColonyRelocationTests.Fixture())
        {
            var adapter = new ClientColonyRelocationCommands(f.Flow); ClientColonyRelocationCommands.Attach(adapter);
            guards = false; Check(RemoteSessionOdysseyGravshipTakeoffPatch.Prefix(engine) && !f.Sync.IsBusy, "Inactive capability started relocation."); guards = true;
            remoteThing = true; Check(!RemoteSessionOdysseyGravshipTakeoffPatch.Prefix(engine) && !f.Sync.IsBusy, "Remote engine started relocation."); remoteThing = false;
            failMap = true; Check(!RemoteSessionOdysseyGravshipTakeoffPatch.Prefix(engine) && warnings == 1 && !f.Mutations.IsPending,
                "Takeoff exception permitted an untracked flight."); failMap = false;
            Check(adapter.Takeoff(null) && adapter.LandAsync(null).IsCompletedSuccessfully, "Missing-map compatibility changed.");
            Check(RemoteSessionOdysseyGravshipTakeoffPatch.Prefix(engine) && f.Sync.IsBusy && f.Registration.Suppressed, "Takeoff bypassed relocation admission/suppression.");
            Check(!RemoteSessionOdysseyGravshipTakeoffPatch.Prefix(engine), "Repeated takeoff bypassed active relocation.");
            homes.Clear(); homes.Add(parentMaps[destination]);
            OdysseyGravshipLandingSnapshotPatch.Prefix(); OdysseyGravshipLandingSnapshotPatch.Prefix();
            Check(OdysseyGravshipLandingSnapshotPatch.SuppressPlayerColonySiteRegistration, "Nested landing failed to suppress registrations.");
            var error = new InvalidOperationException("landing");
            Check(ReferenceEquals(OdysseyGravshipLandingSnapshotPatch.Finalizer(error), error)
                && OdysseyGravshipLandingSnapshotPatch.SuppressPlayerColonySiteRegistration, "Nested finalizer consumed the outer suppression or exception.");
            try
            {
                OdysseyGravshipLandingSnapshotPatch.Postfix(parentMaps[destination]);
                f.Ports.PumpUntil(f.Done.Task).GetAwaiter().GetResult();
            }
            finally { OdysseyGravshipLandingSnapshotPatch.Finalizer(null); }
            Check(f.Preflights == 1 && f.Uploads == 1 && f.Confirms == 1 && f.SentTile == 12 && f.SentLayer == 7
                && !f.Sync.IsBusy && !f.Registration.Suppressed && !OdysseyGravshipLandingSnapshotPatch.SuppressPlayerColonySiteRegistration,
                "Landing did not complete the original flight transaction with its destination layer.");
        }
    }
    private static Settlement Add(int id, PlanetTile tile, bool home)
    {
        var parent = New<Settlement>(); parent.ID = id; parent.Name = "Colony " + id; parent.def = new WorldObjectDef();
        Set(parent, "factionInt", player); Set(parent, "tile", tile);
        var map = New<Map>(); map.mapPawns = New<MapPawns>();
        objects.Add(parent); maps.Add(map); parentMaps[parent] = map; mapParents[map] = parent; if (home) homes.Add(map); return parent;
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool Maps(ref List<Map> __result) { __result = maps; return false; }
    private static bool Player(ref Faction __result) { __result = player; return false; }
    private static bool Objects(ref List<WorldObject> __result) { __result = objects; return false; }
    private static bool ParentMap(MapParent __instance, ref Map? __result) { __result = parentMaps.GetValueOrDefault(__instance); return false; }
    private static bool HasMap(MapParent __instance, ref bool __result) { __result = parentMaps.ContainsKey(__instance); return false; }
    private static bool Parent(Map __instance, ref MapParent __result) { __result = mapParents[__instance]; return false; }
    private static bool Home(Map __instance, ref bool __result) { __result = homes.Contains(__instance); return false; }
    private static bool Colonists(ref int __result) { __result = colonists; return false; }
    private static bool Controlled(ref bool __result) { __result = true; return false; }
    private static bool NoRemote(ref bool __result) { __result = false; return false; }
    private static bool Guards(ref bool __result) { __result = guards; return false; }
    private static bool EngineMap(ref Map? __result)
    { if (failMap) throw new InvalidOperationException("map read"); __result = engineMap; return false; }
    private static bool Remote(ref bool __result) { __result = remoteThing; return false; }
    private static bool Warning() { warnings++; return false; }
    private static bool Skip() => false;
    private static bool Translate(string __0, ref TaggedString __result) { __result = __0; return false; }
    private static bool Vanilla(ref Command __result)
    { vanillaCalls++; var command = new Command_Action(); if (vanillaFailure is not null) command.Disable(vanillaFailure); __result = command; return false; }
    private static void Replacement(Caravan caravan, Command __result)
        => Check(new ClientColonyRelocationCommands(null!).Build(caravan, __result) is null, "Vanilla inspection entered relocation replacement.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
