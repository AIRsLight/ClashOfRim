using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Multiplayer.Players;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class PlayerColonyLocatorTests
{
    private static PlanetTile jumped;
    private static int notices;
    private static bool clicked;
    private static Rect buttonRect;
    private static int closed;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld;
        var harmony = new Harmony("ClashOfRim.Tests.PlayerColonyLocator");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.TilesCount)), prefix: Method(nameof(TileCount)));
            harmony.Patch(AccessTools.GetDeclaredMethods(typeof(CameraJumper)).Single(method => method.Name == nameof(CameraJumper.TryJump)
                && method.GetParameters()[0].ParameterType == typeof(PlanetTile)), prefix: Method(nameof(Jump)));
            harmony.Patch(AccessTools.Method(typeof(Messages), nameof(Messages.Message), new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) }),
                prefix: Method(nameof(Notice)));
            Current.CreatingWorld = null; Current.Game = New<Game>(); Current.Game.World = World();
            float now = 10; int reads = 0, refreshes = 0;
            var markers = new List<ModWorldMapMarkerDto> { Marker(0, 0) };
            var locator = new PlayerColonyLocator(() => { reads++; return markers; }, _ => refreshes++, () => now);
            var target = new ModPlayerSummaryDto { UserId = "bob", ColonyId = "home" };
            Check(locator.CanLocate(target, 1) && reads == 1, "Tile zero or marker fallback was lost.");
            locator.Locate(target, 1);
            Check(jumped.tileId == 0 && reads == 1, "Locate did not reuse its validated target.");
            markers.Clear(); now = 11;
            Check(locator.CanLocate(target, 1) && reads == 1, "Location cache expired before two seconds.");
            now = 12;
            Check(!locator.CanLocate(target, 1) && reads == 2, "Location cache did not expire at two seconds.");
            locator.Locate(target, 1);
            Check(notices == 1 && refreshes == 1, "Missing target did not report and request marker refresh.");
            markers.Add(Marker(1, 0));
            Check(locator.CanLocate(target, 1) && reads == 3, "Failed locate did not evict its negative cache.");
            markers.Clear();
            Check(!locator.CanLocate(target, 2), "Changed player version retained cached locations.");
            markers.Add(Marker(1, 0)); locator.Reset();
            Check(locator.CanLocate(target, 2), "Page reset did not clear negative cache.");
            markers.Clear(); Current.Game.World = World();
            Check(!locator.CanLocate(target, 2), "World replacement reused another world's location.");

            markers.Add(Marker(1, 0)); markers[0].OwnerUserId = "BOB"; locator.Reset();
            Check(!locator.CanLocate(target, 2), "Owner matching stopped being ordinal.");
            markers[0].OwnerUserId = "bob"; markers[0].OwnerColonyId = "other"; locator.Reset();
            Check(!locator.CanLocate(target, 2), "Location borrowed another colony belonging to the same user.");
            markers[0].OwnerColonyId = "home"; markers[0].Kind = "RuntimeCaravan"; locator.Reset();
            Check(!locator.CanLocate(target, 2), "Non-colony marker became a player colony.");
            markers.Clear(); markers.Add(Marker(25, 7)); locator.Reset();
            Check(locator.CanLocate(target, 2), "Non-surface colony location borrowed the surface layer's tile count.");
            locator.Locate(target, 2);
            Check(jumped.tileId == 25 && jumped.Layer.LayerID == 7, "Locate flattened its target layer.");
            markers[0].Tile = 30; locator.Reset();
            Check(!locator.CanLocate(target, 2), "Out-of-layer tile was accepted.");
            markers.Clear(); markers.Add(Marker(1, 99)); markers[0].Label = "A";
            markers.Add(Marker(1, 7)); markers[1].Label = "B"; locator.Reset();
            Check(locator.CanLocate(target, 2), "Invalid layer hid a later valid colony marker.");
            FactionNavigation(harmony, locator, markers);
            Current.Game.World.grid = null; locator.Reset();
            Check(!locator.CanLocate(target, 2), "Missing geometry retained a location.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating;
            notices = 0; jumped = PlanetTile.Invalid;
        }
    }

    private static void FactionNavigation(Harmony harmony, PlayerColonyLocator locator, List<ModWorldMapMarkerDto> markers)
    {
        var previous = PlayerFactionColonyNavigation.Current;
        var ownersField = AccessTools.Field(typeof(PlayerFactionProxyUtility), "ProxyOwnerUserIdsByLoadId");
        var previousOwners = ownersField.GetValue(null);
        try
        {
            // Install the actual transpiler against the current game assembly. Its
            // native button call must bind; no copied faction-row geometry is used.
            harmony.CreateClassProcessor(typeof(PlayerFactionRowNavigationPatch)).Patch();
            harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonInvisible), new[] { typeof(Rect), typeof(bool) }),
                prefix: Method(nameof(Button)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.MainTabsRoot)), prefix: Method(nameof(Tabs)));
            harmony.Patch(AccessTools.Method(typeof(MainTabsRoot), nameof(MainTabsRoot.EscapeCurrentTab)), prefix: Method(nameof(CloseTab)));
            ownersField.SetValue(null, new Dictionary<int, string> { [7201] = "bob", [7202] = "other" });
            var faction = new Faction { loadID = 7201, Name = "同名阵营", def = new FactionDef { defName = PlayerFactionProxyUtility.ProxyFactionDefName } };
            var other = new Faction { loadID = 7202, Name = faction.Name, def = faction.def };
            var npc = new Faction { loadID = 7203, Name = faction.Name, def = new FactionDef { defName = "NPC" } };
            var self = new Faction { loadID = 7204, def = new FactionDef { defName = "Player", isPlayer = true } };
            bool active = true;
            PlayerFactionColonyNavigation.Attach(new(() => active, locator));
            var rect = new Rect(17, 230, 391, 81);
            markers.Clear(); markers.Add(Marker(25, 7));
            var second = Marker(4, 0); second.OwnerUserId = "other"; markers.Add(second);
            clicked = false; jumped = PlanetTile.Invalid; closed = 0;
            Check(!Click(faction) && !jumped.Valid, "Rendering a faction row caused navigation.");
            clicked = true;
            Check(!Click(faction) && jumped.tileId == 25 && jumped.Layer.LayerID == 7,
                "Player faction click did not locate its layered colony or also opened its info card.");
            Check(buttonRect == rect, "Navigation changed the native button hit area.");
            Check(!Click(other) && jumped.tileId == 4, "Equal faction names confused account ownership.");
            markers[0].Tile = 26;
            Check(!Click(faction) && jumped.tileId == 26, "Faction click retained a colony location from before relocation.");
            Check(closed == 3, "Successful navigation left the faction tab covering the target.");
            jumped = PlanetTile.Invalid;
            Check(Click(npc) && Click(self) && !jumped.Valid, "NPC or self faction clicks lost native info cards.");
            active = false;
            Check(Click(faction) && !jumped.Valid, "Disconnected faction click navigated stale multiplayer markers.");
            active = true; markers.Clear(); int previousNotices = notices;
            Check(!Click(faction) && !jumped.Valid && notices == previousNotices + 1,
                "Missing colony did not use the locator's unavailable feedback.");
            Check(closed == 3, "An unavailable or non-player target closed the faction tab.");
            PlayerFactionColonyNavigation.Attach(null);
            Check(Click(faction), "Unbound faction UI suppressed the native info card.");

            bool Click(Faction value) => PlayerFactionRowNavigationPatch.FactionButton(rect, false, value);
        }
        finally
        {
            PlayerFactionColonyNavigation.Attach(previous); ownersField.SetValue(null, previousOwners);
            clicked = false;
        }
    }

    private static World World()
    {
        var world = New<World>(); world.grid = New<WorldGrid>();
        var surface = ClientOccupiedColonySitesRuntimeTests.Layer(0, 20);
        AccessTools.Field(typeof(WorldGrid), "surface").SetValue(world.grid, surface);
        AccessTools.Field(typeof(WorldGrid), "planetLayers").SetValue(world.grid,
            new Dictionary<int, PlanetLayer> { [0] = surface, [7] = ClientOccupiedColonySitesRuntimeTests.Layer(7, 30) });
        return world;
    }
    private static ModWorldMapMarkerDto Marker(int tile, int layer) =>
        new() { Kind = "TradeableColony", OwnerUserId = "bob", OwnerColonyId = "home", Tile = tile, TileLayerId = layer };
    private static bool TileCount(PlanetLayer __instance, ref int __result) { __result = __instance.Tiles.Count; return false; }
    private static bool Jump(PlanetTile __0) { jumped = __0; return false; }
    private static bool Notice() { notices++; return false; }
    private static bool Button(Rect __0, bool __1, ref bool __result)
    { buttonRect = __0; Check(!__1, "Navigation changed mouseover sound behavior."); __result = clicked; return false; }
    private static bool Tabs(ref MainTabsRoot __result) { __result = New<MainTabsRoot>(); return false; }
    private static bool CloseTab() { closed++; return false; }
    private static HarmonyMethod Method(string name) => new(typeof(PlayerColonyLocatorTests), name);
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
