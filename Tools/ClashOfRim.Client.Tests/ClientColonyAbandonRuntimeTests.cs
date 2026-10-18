using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Profile;

internal static class ClientColonyAbandonRuntimeTests
{
    private static readonly List<WorldObject> objects = new();
    private static int menus;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld;
        var harmony = new Harmony("ClashOfRim.Tests.ColonyAbandonRuntime");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.AllWorldObjects)),
                prefix: new HarmonyMethod(typeof(ClientColonyAbandonRuntimeTests), nameof(Objects)));
            harmony.Patch(AccessTools.Method(typeof(GenScene), nameof(GenScene.GoToMainMenu)),
                prefix: new HarmonyMethod(typeof(ClientColonyAbandonRuntimeTests), nameof(Menu)));
            Current.CreatingWorld = null;
            var live = New<Game>(); var world = New<World>(); live.World = world; Current.Game = live;
            var player = New<Faction>(); player.def = new FactionDef { isPlayer = true };
            world.factionManager = New<FactionManager>(); Set(world.factionManager, "ofPlayer", player);
            world.worldObjects = New<WorldObjectsHolder>();
            var colony = New<Settlement>(); colony.Name = "Colony"; colony.def = new WorldObjectDef(); Set(colony, "factionInt", player);
            objects.Clear(); objects.Add(colony); menus = 0;
            var target = new ClientColonyAbandonTarget(colony);
            Check(target.IsCurrent && target.IsAvailable && target.Label == colony.LabelCap, "Current player colony or label lost.");
            Set(colony, "factionInt", New<Faction>());
            Check(!target.IsAvailable, "Foreign colony was admitted.");
            Set(colony, "factionInt", player); objects.Clear();
            Check(!target.IsAvailable, "Detached colony was admitted.");
            var replacement = New<Settlement>(); replacement.Name = colony.Name; Set(replacement, "factionInt", player); objects.Add(replacement);
            Check(!target.IsAvailable, "Same-named replacement adopted old confirmation.");
            objects.Add(colony); Set(colony, "destroyed", true);
            Check(!target.IsAvailable, "Destroyed colony was admitted.");
            Set(colony, "destroyed", false);
            target.ReturnToMenu(); Check(menus == 1, "Current target did not dispatch menu transition.");
            live.World = New<World>();
            Check(!target.IsCurrent && !target.IsAvailable, "World replacement retained confirmation.");
            target.ReturnToMenu(); Check(menus == 1, "Old world transitioned to menu.");
            live.World = world; Current.Game = New<Game>(); Current.Game.World = world;
            Check(!target.IsCurrent && !target.IsAvailable, "Same-world replacement game retained confirmation.");
            target.ReturnToMenu(); Check(menus == 1, "Old game transitioned to menu.");
            Current.Game = null; Current.CreatingWorld = world;
            Check(!target.IsCurrent && !target.IsAvailable, "Creating world adopted old confirmation.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating; objects.Clear();
        }
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool Objects(ref List<WorldObject> __result) { __result = objects; return false; }
    private static bool Menu() { menus++; return false; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
