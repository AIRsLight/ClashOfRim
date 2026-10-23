using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class RemoteMapLoadedItemTests
{
    private static readonly IntVec3 Cell = new(5, 0, 5);
    private static int moved;

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RemoteMapLoadedItems");
        var previousState = Current.ProgramState;
        try
        {
            Current.ProgramState = ProgramState.Playing;
            // Keep GenSpawn's real overflow decision and native grids. Only replace
            // subsequent world placement and the full Thing.SpawnSetup environment.
            harmony.Patch(AccessTools.Method(typeof(GenPlace), nameof(GenPlace.TryPlaceThing), new[]
            {
                typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode),
                typeof(Action<Thing, int>), typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int)
            }), prefix: Method(nameof(MoveAside)));

            var root = Map(new RemoteSessionMapParent());
            using (RemoteMapProjectionLoadScope.Begin()) SpawnStacks(root, 3, true);
            Check(moved == 2 && Cell.GetItemCount(root) == 1, "Native control failed to reproduce pre-shelf scattering.");

            Current.ProgramState = ProgramState.MapInitializing;
            var nativeLoad = Map(new MapParent());
            moved = 0; SpawnStacks(nativeLoad, 3, true);
            Check(moved == 0 && Cell.GetItemCount(nativeLoad) == 3, "Native save loading no longer preserves pre-shelf stacks.");
            Current.ProgramState = ProgramState.Playing;

            harmony.CreateClassProcessor(typeof(RemoteMapLoadedItemSpawnPatch)).Patch();
            root = Map(new RemoteSessionMapParent());
            var child = Map(new PocketMapParent { sourceMap = root });
            var nested = Map(new PocketMapParent { sourceMap = child });
            var local = Map(new MapParent());
            var localPocket = Map(new PocketMapParent { sourceMap = local });
            int capacity = 2;
            foreach (var map in new[] { root, child, nested })
            using (RemoteMapProjectionLoadScope.Begin())
            {
                capacity++;
                moved = 0; SpawnStacks(map, capacity, true);
                Check(moved == 0 && Cell.GetItemCount(map) == capacity, "Remote saved stacks scattered before their shelf loaded.");
                Check(Cell.GetMaxItemsAllowedInCell(map) == 1 && Current.ProgramState == ProgramState.Playing,
                    "Restore changed ordinary capacity queries or global program state.");
                var shelf = Shelf(map, capacity);
                Check(Cell.GetMaxItemsAllowedInCell(map) == capacity, "Loaded shelf capacity changed.");
                SpawnStacks(map, 1, false);
                Check(moved == 1 && Cell.GetItemCount(map) == capacity, "A new item bypassed shelf capacity during restoration.");
                map.edificeGrid.DeRegister(shelf);
                Check(Cell.GetMaxItemsAllowedInCell(map) == 1, "Removed shelf retained an elevated cell limit.");
            }
            foreach (var map in new[] { local, localPocket })
            using (RemoteMapProjectionLoadScope.Begin())
            {
                moved = 0; SpawnStacks(map, 3, true);
                Check(moved == 2 && Cell.GetItemCount(map) == 1, "Remote load scope relaxed a local map's capacity.");
            }
            var packed = Map(new RemoteSessionMapParent());
            using (RemoteMapProjectionLoadScope.Begin())
            using (RemoteMapProjectionLoadScope.Begin())
            {
                moved = 0; SpawnStacks(packed, 30, true);
                Check(moved == 0 && Cell.GetItemCount(packed) == 30, "Higher-capacity storage gained a hard-coded shelf limit.");
            }
            try
            {
                using var scope = RemoteMapProjectionLoadScope.Begin();
                throw new InvalidOperationException("load fault");
            }
            catch (InvalidOperationException ex) when (ex.Message == "load fault") { }
            Check(!RemoteMapProjectionLoadScope.Active, "Failed or nested load leaked the restore scope.");
            moved = 0; SpawnStacks(packed, 1, true);
            Check(moved == 1, "Later respawn outside remote loading kept the exemption.");
        }
        finally { harmony.UnpatchAll(harmony.Id); Current.ProgramState = previousState; moved = 0; }
    }

    private static Map Map(MapParent parent)
    {
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(20, 1, 20), parent = parent };
        map.cellIndices = new CellIndices(map);
        map.thingGrid = new ThingGrid(map); map.edificeGrid = new EdificeGrid(map);
        return map;
    }
    private static Building Shelf(Map map, int capacity)
    {
        var shelf = new Building
        {
            def = new ThingDef { defName = "Shelf", category = ThingCategory.Building, building = new BuildingProperties { maxItemsInCell = capacity } },
            Position = Cell
        };
        map.edificeGrid.Register(shelf);
        return shelf;
    }
    private static void SpawnStacks(Map map, int count, bool loading)
    {
        for (int i = 0; i < count; i++)
        {
            var item = new GridItem { def = new ThingDef { defName = "Steel", category = ThingCategory.Item }, stackCount = 75 };
            GenSpawn.Spawn(item, Cell, map, Rot4.North, WipeMode.FullRefund, respawningAfterLoad: loading);
        }
        Check(Cell.GetThingList(map).All(t => t.stackCount == 75), "Restoration changed saved stack sizes.");
    }
    private sealed class GridItem : Thing
    {
        private Map grid = null!;
        public override void SpawnSetup(Map map, bool respawningAfterLoad) { grid = map; grid.thingGrid.Register(this); }
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish) => grid.thingGrid.Deregister(this, doEvenIfDespawned: true);
    }
    private static bool MoveAside(ref bool __result) { moved++; __result = true; return false; }
    private static HarmonyMethod Method(string name) => new(typeof(RemoteMapLoadedItemTests), name);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
