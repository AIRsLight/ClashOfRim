using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.MainMenu;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class ClientOccupiedColonySitesRuntimeTests
{
    private static readonly List<(PlanetTile From, PlanetTile To)> queries = new();
    private static bool failNeighbors;
    internal static void Run()
    {
        var game = Current.Game; var creating = Current.CreatingWorld; var culture = CultureInfo.CurrentCulture;
        var binding = ClientWorldSiteReservations.Current;
        var harmony = new Harmony("ClashOfRim.Tests.OccupiedColonySites");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.TilesCount)),
                prefix: new HarmonyMethod(typeof(ClientOccupiedColonySitesRuntimeTests), nameof(TileCount)));
            harmony.Patch(AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.IsNeighborOrSame)),
                prefix: new HarmonyMethod(typeof(ClientOccupiedColonySitesRuntimeTests), nameof(Neighbors)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Selected)),
                prefix: new HarmonyMethod(typeof(ClientOccupiedColonySitesRuntimeTests), nameof(Selected)));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientOccupiedColonySitesRuntimeTests), nameof(Text)));
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); queries.Clear();
                Current.CreatingWorld = null; Current.Game = null;
                var runtime = new ClientOccupiedColonySitesRuntime(); var menu = runtime.Bind();
                Check(menu.IsCurrent && !menu.CanReserve(0, 0), "Menu geometry unexpectedly available."); menu.EnsureProxy("remote");
                var world = New<World>(); var live = New<Game>(); live.World = world; Current.Game = live;
                var surface = Layer(0, 20); var underground = Layer(7, 30); var orbit = Layer(9, 40, space: true);
                world.grid = New<WorldGrid>(); Set(world.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [7] = underground, [9] = orbit, [0] = surface }); Set(world.grid, "surface", surface);
                var bound = runtime.Bind(); Check(!menu.IsCurrent && bound.IsCurrent, "Game replacement did not change geometry binding.");
                Check(bound.CanReserve(25, 7) && !bound.CanReserve(25, 0) && !bound.CanReserve(10, 9)
                    && !bound.CanReserve(10, 3) && !bound.CanReserve(-1, 7), "Layer validity/space semantics changed.");
                var f = new ClientOccupiedColonySitesTests.Fixture(runtime);
                f.Replace(new[] { new ModPlayerColonySiteDto { Tile = 10, TileLayerId = 7 }, new ModPlayerColonySiteDto { Tile = 25, TileLayerId = 7 } });
                var reservations = new ClientWorldSiteReservations(f.Cache); ClientWorldSiteReservations.Attach(reservations);
                Check(!reservations.IsBlocked(new PlanetTile(10, 0), out _), "Surface query borrowed another layer.");
                Check(reservations.IsBlocked(new PlanetTile(10, 7), out var same) && same!.TileLayerId == 7, "PlanetTile adapter lost its layer.");
                Check(reservations.IsBlocked(new PlanetTile(11, 7), out _) && !reservations.IsBlocked(new PlanetTile(12, 7), out _), "Adjacency forwarding changed.");
                Check(reservations.IsBlocked(new PlanetTile(25, 7), out _), "Non-surface layer borrowed surface size.");
                Check(!reservations.IsBlocked(new PlanetTile(10, 9), out _) && !reservations.IsBlocked(PlanetTile.Invalid, out _), "Space or invalid tile reserved.");
                Check(queries.Count != 0 && queries.All(q => q.From.Layer == underground && q.To.Layer == underground), "Engine received cross-layer adjacency.");
                Patches();
                live.World = New<World>(); Check(!bound.IsCurrent, "World replacement retained old geometry.");
                Current.Game = null; Current.CreatingWorld = world;
                var creatingBound = runtime.Bind(); Check(creatingBound.IsCurrent && creatingBound.CanReserve(25, 7), "CreatingWorld geometry missing.");
                Current.CreatingWorld = New<World>(); Check(!creatingBound.IsCurrent, "CreatingWorld replacement retained old geometry.");
                Current.CreatingWorld = null;
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id); Current.Game = game; Current.CreatingWorld = creating;
            CultureInfo.CurrentCulture = culture; queries.Clear(); ClientSessionWork.Reset();
            ClientWorldSiteReservations.Attach(binding); failNeighbors = false;
        }
    }
    private static void Patches()
    {
        var tile = new PlanetTile(10, 7); bool valid = false; var reason = new StringBuilder("vanilla"); int before = queries.Count;
        ClashOfRimStartingSiteValidationPatch.Postfix(tile, reason, ref valid);
        Check(!valid && queries.Count == before && reason.ToString() == "vanilla", "Vanilla refusal did not short-circuit reservation lookup.");
        valid = true; ClashOfRimStartingSiteValidationPatch.Postfix(tile, reason, ref valid);
        Check(!valid && reason.Length > "vanilla".Length, "Starting-site patch missed reserved layer or rejection text.");
        int calls = 0; Predicate<PlanetTile> candidate = _ => { calls++; return false; };
        ClashOfRimNewSiteTileReservationPatch.Prefix(ref candidate); before = queries.Count;
        Check(!candidate(tile) && calls == 1 && queries.Count == before, "New-site filter lost its original validator short circuit.");
        candidate = null!; ClashOfRimTraversalTileReservationPatch.Prefix(ref candidate);
        Check(!candidate(tile) && candidate(new PlanetTile(12, 7)) && candidate(new PlanetTile(10, 0)), "Traversal filter lost reserved/adjacent layer policy.");
        failNeighbors = true;
        try { candidate(tile); throw new Exception("Reservation failure silently admitted a tile."); }
        catch (InvalidOperationException ex) when (ex.Message == "geometry") { }
        finally { failNeighbors = false; }
        var original = ClientWorldSiteReservations.Current; ClientWorldSiteReservations.Attach(null);
        valid = true; ClashOfRimStartingSiteValidationPatch.Postfix(tile, reason, ref valid);
        Check(valid && candidate(tile), "Unbound reservations changed vanilla admission."); ClientWorldSiteReservations.Attach(original);
        Check(ClientWorldSiteReservations.Format(new() { UserId = "alice", ColonyId = "colony", Tile = 10 }) == "alice/colony@10"
            && ClientWorldSiteReservations.Format(new() { UserId = "alice", Label = "基地", Tile = 10 }) == "alice/基地@10"
            && ClientWorldSiteReservations.Format(new() { UserId = "alice", Tile = 10 }) == "alice@10", "Reservation label fallback changed.");
    }
    internal static SurfaceLayer Layer(int id, int count, bool space = false)
    {
        var layer = New<SurfaceLayer>(); Set(layer, "layerId", id); Set(layer, "def", new PlanetLayerDef { defName = "layer" + id, isSpace = space });
        Set(layer, "tiles", Enumerable.Range(0, count).Select(i => (Tile)new SurfaceTile { tile = new PlanetTile(i, id) }).ToList()); return layer;
    }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
    private static bool TileCount(PlanetLayer __instance, ref int __result) { __result = __instance.Tiles.Count; return false; }
    private static bool Neighbors(PlanetTile __0, PlanetTile __1, ref bool __result)
    { if (failNeighbors) throw new InvalidOperationException("geometry"); queries.Add((__0, __1)); __result = __0.Layer == __1.Layer && Math.Abs(__0.tileId - __1.tileId) <= 1; return false; }
    private static bool Selected() => throw new InvalidOperationException("Reservation must not read the selected UI layer.");
    private static bool Text(string __0, ref string __result) { __result = __0; return false; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
