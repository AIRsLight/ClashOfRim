using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static class StartingSiteColonyFilterTests
{
    private static readonly HashSet<Window> pages = new();
    private static WorldSelector selector = null!;
    private static List<WorldObject> underMouse = new();
    private static Dictionary<string, string> translations = new();
    private static bool checkboxValue;
    private static Rect checkboxRect;

    internal static async Task RunAsync()
    {
        var previousGame = Current.Game; var creating = Current.CreatingWorld;
        var previousPages = ClientWorldSetupPages.Current;
        var harmony = new Harmony("ClashOfRim.Tests.StartingSiteColonyFilter");
        void Patch(MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(StartingSiteColonyFilterTests), prefix));
        try
        {
            Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)), nameof(Stack));
            Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.IsOpen), new[] { typeof(Window) }), nameof(IsOpen));
            Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WorldSelector)), nameof(Selector));
            Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.TilesCount)), nameof(TileCount));
            Patch(AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.IsNeighborOrSame)), nameof(Neighbors));
            Patch(AccessTools.Method(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.CanCreateMapAt)), nameof(CanCreate));
            Patch(AccessTools.Method(typeof(GenWorldUI), nameof(GenWorldUI.WorldObjectsUnderMouse)), nameof(Mouse));
            foreach (var type in new[] { typeof(StartingSiteFilterRefreshPatch), typeof(StartingSiteFilterStaticIconsPatch),
                typeof(StartingSiteFilterExpandedIconsPatch), typeof(StartingSiteFilterDynamicIconsPatch), typeof(StartingSiteFilterMousePatch) })
                harmony.CreateClassProcessor(type).Patch();

            Current.CreatingWorld = null; Current.Game = New<Game>();
            var world = New<World>(); Current.Game.World = world;
            world.worldObjects = new WorldObjectsHolder(); world.renderer = New<WorldRenderer>(); world.grid = New<WorldGrid>();
            var surface = ClientOccupiedColonySitesRuntimeTests.Layer(0, 20);
            var underground = ClientOccupiedColonySitesRuntimeTests.Layer(7, 20);
            foreach (var layer in new[] { surface, underground })
                foreach (var tile in layer.Tiles) tile.PrimaryBiome = new BiomeDef { canBuildBase = true, implemented = true };
            Set(world.grid, "surface", surface);
            Set(world.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = surface, [7] = underground });
            selector = new WorldSelector();
            var layers = DrawLayers(surface);
            var undergroundLayers = DrawLayers(underground);

            var npc = Settlement(5, 0, "NPC"); var npcOtherLayer = Settlement(5, 7, "NPC");
            var own = Settlement(11, 0, "Player", player: true);
            var proxy = Settlement(13, 0, PlayerFactionProxyUtility.ProxyFactionDefName);
            var remote = new RemoteColonyMapParent { RuntimeKind = "TradeableColony", OwnerUserId = "bob", def = new WorldObjectDef() };
            Set(remote, "tile", new PlanetTile(15, 0));
            var source = new WorldObject[] { npc, npcOtherLayer, own, proxy, remote };
            world.worldObjects.AllWorldObjects.AddRange(source);
            world.worldObjects.SettlementBases.Add(npc); world.worldObjects.SettlementBases.Add(npcOtherLayer);
            underMouse = source.ToList();
            var baseline = new[] { 4, 5, 6, 9 }.Select(tile => TileFinder.IsValidTileForNewSettlement(new PlanetTile(tile))).ToArray();
            Check(baseline.SequenceEqual(new[] { false, false, false, true }), "Native settlement validation fixture is invalid.");

            var f = new ClientWorldSetupLifecycleTests.Fixture();
            var adapter = new ClientWorldSetupPages(f.Flow, f.Core.Setup); ClientWorldSetupPages.Attach(adapter);
            var page = New<Page_SelectStartingSite>(); pages.Add(page);
            await f.Pump(adapter.OpenedAsync(page));
            Check(adapter.SiteFilter is null, "Ordinary single-player setup received a multiplayer filter.");
            adapter.Closed(page);
            f.Core.Setup.Prepare(new() { Result = new() { Accepted = true } });
            await f.Pump(adapter.OpenedAsync(page));
            var filter = adapter.SiteFilter ?? throw new InvalidOperationException("Admitted setup did not create its filter.");
            Check(!filter.Enabled && !StartingSiteColonyFilter.Hides(npc), "Filter did not start disabled.");
            selector.SelectedTile = npc.Tile; selector.SelectedObjects.Add(npc);
            filter.SetEnabled(true);
            Check(layers[0].Dirty && layers[1].Dirty && !layers[2].Dirty
                && undergroundLayers[0].Dirty && undergroundLayers[1].Dirty && !undergroundLayers[2].Dirty,
                "Display toggle rebuilt terrain or missed static icons on a planet layer.");
            Check(selector.SelectedObjects.Count == 0 && selector.SelectedTile == npc.Tile, "Hiding a selected base lost its tile or left brackets.");
            Check(StartingSiteColonyFilter.Hides(npc) && StartingSiteColonyFilter.Hides(npcOtherLayer)
                && source.Skip(2).All(value => !StartingSiteColonyFilter.Hides(value)), "Filter confused NPC, player or proxy settlements/layers.");

            var display = new List<WorldObject>(); StartingSiteFilterExpandedIconsPatch.AddVisible(display, source);
            Check(display.SequenceEqual(source.Skip(2)), "Expanded icon list leaked NPC icons or lost player colonies.");
            var expandable = AccessTools.Method(typeof(WorldDrawLayer_WorldObjects_Expandable), "ShouldSkip");
            Check((bool)expandable.Invoke(layers[0], new object[] { npc })!
                && !(bool)expandable.Invoke(layers[0], new object[] { own })!, "Native static icon predicate missed the filter.");
            StartingSiteFilterDynamicIconsPatch.DrawVisible(npc); StartingSiteFilterDynamicIconsPatch.DrawVisible(own);
            Check(npc.Draws == 0 && own.Draws == 1, "Dynamic icon filter changed virtual dispatch or drew NPCs.");
            Check(GenWorldUI.WorldObjectsUnderMouse(Vector2.zero).SequenceEqual(source.Skip(2)), "Invisible NPCs remained hover targets.");
            Check(world.worldObjects.AllWorldObjects.SequenceEqual(source) && world.worldObjects.SettlementBaseAt(npc.Tile) == npc,
                "Display filtering removed authoritative world objects.");
            Check(new[] { 4, 5, 6, 9 }.Select(tile => TileFinder.IsValidTileForNewSettlement(new PlanetTile(tile))).SequenceEqual(baseline)
                && !TileFinder.IsValidTileForNewSettlement(new PlanetTile(4, 7)), "Hidden settlements bypassed occupied/adjacent tile checks.");
            filter.SetEnabled(false); display.Clear(); StartingSiteFilterExpandedIconsPatch.AddVisible(display, source);
            Check(display.SequenceEqual(source) && !(bool)expandable.Invoke(layers[0], new object[] { npc })!, "Disabling did not restore NPC icons.");
            DrawControl(filter);

            foreach (string change in new[] { "close", "removed", "user", "game", "world", "unavailable", "replace", "engine-world" })
            {
                adapter.Closed(page); pages.Add(page);
                var next = new ClientWorldSetupLifecycleTests.Fixture();
                next.Core.Setup.Prepare(new() { Result = new() { Accepted = true } });
                adapter = new(next.Flow, next.Core.Setup); ClientWorldSetupPages.Attach(adapter);
                await next.Pump(adapter.OpenedAsync(page)); filter = adapter.SiteFilter!; filter.SetEnabled(true);
                foreach (var layer in layers.Concat(undergroundLayers)) Set(layer, "dirty", false);
                WorldDrawLayer[] otherLayers = Array.Empty<WorldDrawLayer>();
                switch (change)
                {
                    case "close": adapter.Closed(page); break;
                    case "removed": pages.Remove(page); break;
                    case "replace": ClientWorldSetupPages.Attach(new(next.Flow, next.Core.Setup)); break;
                    case "engine-world":
                        var otherWorld = New<World>(); otherWorld.grid = New<WorldGrid>();
                        var otherSurface = ClientOccupiedColonySitesRuntimeTests.Layer(0, 20);
                        otherLayers = DrawLayers(otherSurface);
                        Set(otherWorld.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = otherSurface });
                        Current.Game.World = otherWorld;
                        break;
                    default: next.Core.Change(change); break;
                }
                filter.Refresh();
                Check(!filter.Enabled && !StartingSiteColonyFilter.Hides(npc) && layers[0].Dirty && layers[1].Dirty
                    && undergroundLayers[0].Dirty && undergroundLayers[1].Dirty && !layers[2].Dirty
                    && !undergroundLayers[2].Dirty && otherLayers.All(layer => !layer.Dirty),
                    "Filter leaked or did not restore meshes after " + change);
                Current.Game.World = world;
            }
            PreparedState();
        }
        finally
        {
            ClientWorldSetupPages.Attach(previousPages); harmony.UnpatchAll(harmony.Id);
            Current.Game = previousGame; Current.CreatingWorld = creating; pages.Clear(); underMouse.Clear();
        }
    }

    private static void DrawControl(StartingSiteColonyFilter filter)
    {
        var previousLanguage = LanguageDatabase.activeLanguage;
        int previousWidth = UI.screenWidth;
        LanguageDatabase.activeLanguage = new LoadedLanguage("English") { info = new LanguageInfo { canBeTiny = false } };
        var harmony = new Harmony("ClashOfRim.Tests.StartingSiteFilterControl");
        var font = Text.Font; var anchor = Text.Anchor; bool wrap = Text.WordWrap;
        void Patch(MethodBase method, string name) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(StartingSiteColonyFilterTests), name));
        try
        {
            Patch(AccessTools.Method(typeof(Text), nameof(Text.CalcHeight), new[] { typeof(string), typeof(float) }), nameof(Height));
            Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.CheckboxLabeled)), nameof(Checkbox));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Widgets)).Where(m => m.Name == nameof(Widgets.DrawWindowBackground))) Patch(method, nameof(Skip));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(TooltipHandler)).Where(m => m.Name == nameof(TooltipHandler.TipRegion))) Patch(method, nameof(Skip));
            Patch(AccessTools.Method(typeof(GenUI), nameof(GenUI.AbsorbClicksInRect)), nameof(Skip));
            Patch(AccessTools.Method(typeof(ClashOfRimText), nameof(ClashOfRimText.Key), new[] { typeof(string) }), nameof(Translate));
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Languages"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Missing shipped translations.");
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root.FullName, "Languages"), "ClashOfRim.xml", SearchOption.AllDirectories))
            foreach (int width in new[] { 320, 640, 1280, 1920 })
            {
                translations = XDocument.Load(file).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
                UI.screenWidth = width; checkboxValue = true;
                Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.LowerRight; Text.WordWrap = false;
                filter.Draw();
                Check(filter.Enabled && checkboxRect.xMin >= 0 && checkboxRect.xMax <= width && checkboxRect.height >= 20,
                    "Translated control overflowed the screen or failed to enable the filter.");
                Check(Text.Font == GameFont.Medium && Text.Anchor == TextAnchor.LowerRight && !Text.WordWrap,
                    "Filter control leaked text style to other windows.");
                checkboxValue = false; filter.Draw(); Check(!filter.Enabled, "Control could not restore NPC display.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap; translations.Clear(); LanguageDatabase.activeLanguage = previousLanguage; UI.screenWidth = previousWidth; }
    }

    private static void PreparedState()
    {
        var f = new ClientWorldSetupLifecycleTests.Fixture(); var setup = f.Core.Setup;
        setup.Prepare(new() { Result = new() { Accepted = true }, RequiresInitialWorldConfiguration = true });
        setup.TakeInitial(); Check(setup.ColonyPrepared, "Initial world submission hid the setup display control.");
        setup.Prepare(new() { Result = new() { Accepted = true }, WorldConfigured = true, WorldConfiguration = new() });
        setup.AttachSubstrate(setup.CaptureGeneration()!, new(1, "", "", "", Array.Empty<byte>()));
        setup.TakeGeneration(); Check(setup.ColonyPrepared, "Consuming world generation hid the setup display control.");
        f.Core.Change("user"); Check(!setup.ColonyPrepared, "Prepared flag crossed account identity.");
        setup.Prepare(new() { Result = new() { Accepted = true }, HasExistingColony = true });
        Check(!setup.ColonyPrepared, "Existing colony load enabled starting-site controls.");
        setup.Prepare(new() { Result = new() { Accepted = false } });
        Check(!setup.ColonyPrepared, "Rejected preparation enabled starting-site controls.");
    }
    private sealed class DrawnSettlement : Settlement { internal int Draws; public override void Draw() => Draws++; }
    private static DrawnSettlement Settlement(int tile, int layer, string faction, bool player = false)
    {
        var value = new DrawnSettlement { def = new WorldObjectDef { expandingIcon = true } };
        Set(value, "tile", new PlanetTile(tile, layer));
        Set(value, "factionInt", new Faction { def = new FactionDef { defName = faction, isPlayer = player } }); return value;
    }
    private static bool Stack(ref WindowStack __result) { __result = New<WindowStack>(); return false; }
    private static bool IsOpen(Window __0, ref bool __result) { __result = pages.Contains(__0); return false; }
    private static bool Selector(ref WorldSelector __result) { __result = selector; return false; }
    private static WorldDrawLayer[] DrawLayers(PlanetLayer layer)
    {
        WorldDrawLayer[] layers = { New<WorldDrawLayer_WorldObjects_Expandable>(),
            New<WorldDrawLayer_WorldObjects_NonExpandable>(), New<WorldDrawLayer_Terrain>() };
        Set(layer, "<WorldDrawLayers>k__BackingField", layers.ToList());
        return layers;
    }
    private static bool TileCount(PlanetLayer __instance, ref int __result) { __result = __instance.Tiles.Count; return false; }
    private static bool Neighbors(PlanetTile __0, PlanetTile __1, ref bool __result)
    { __result = __0.Layer == __1.Layer && Math.Abs(__0.tileId - __1.tileId) <= 1; return false; }
    private static bool CanCreate(ref bool __result) { __result = true; return false; }
    private static bool Mouse(ref List<WorldObject> __result) { __result = underMouse.ToList(); return false; }
    private static bool Height(string __0, float __1, ref float __result)
    { __result = 20f * (float)Math.Ceiling(__0.Length * 14f / __1); return false; }
    private static bool Checkbox(Rect __0, string __1, ref bool __2)
    {
        Check(__1 == translations["ClashOfRim.StartingSite.OnlyPlayerColonies"] && Text.WordWrap,
            "Checkbox label was untranslated or wrapping was disabled.");
        checkboxRect = __0; __2 = checkboxValue; return false;
    }
    private static bool Translate(string __0, ref string __result)
    { __result = translations[__0]; Check(!string.IsNullOrWhiteSpace(__result), "Empty filter translation."); return false; }
    private static bool Skip() => false;
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object value, string field, object data) => AccessTools.Field(value.GetType(), field).SetValue(value, data);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
