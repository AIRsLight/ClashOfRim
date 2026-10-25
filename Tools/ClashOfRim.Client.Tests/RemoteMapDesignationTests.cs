using System;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class RemoteMapDesignationTests
{
    internal static void Run()
    {
        using var definitions = new DefDatabaseTestScope<DesignationDef>();
        var harmony = new Harmony("ClashOfRim.Tests.RemoteMapDesignations");
        try
        {
            // Keep the native designation manager, cell/thing indexes and removal
            // notifications. Only suppress visual effects and diagnostic logging.
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(FleckMaker)).Where(m => m.Name == nameof(FleckMaker.ThrowMetaPuffs)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RemoteMapDesignationTests), nameof(Skip)));
            harmony.Patch(AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), prefix: new HarmonyMethod(typeof(RemoteMapDesignationTests), nameof(Skip)));
            var mine = Def("Mine", TargetType.Cell);
            var vein = Def("MineVein", TargetType.Cell);
            var custom = Def("ThirdPartyPersistentOrder", TargetType.Cell);
            custom.designateCancelable = false;
            var hunt = Def("Hunt", TargetType.Thing);
            var construction = new[] { "Build", "Repair", "Deconstruct", "Uninstall" }.Select(n => Def(n, TargetType.Thing)).ToArray();
            definitions.Replace(new[] { mine, vein, custom, hunt }.Concat(construction));

            var local = Map(new MapParent());
            var remote = Map(new RemoteSessionMapParent());
            var pocket = Map(new PocketMapParent { sourceMap = remote });
            var a = new IntVec3(1, 0, 1); var b = new IntVec3(2, 0, 2);
            local.designationManager.AddDesignation(new Designation(a, mine));
            pocket.designationManager.AddDesignation(new Designation(a, mine));
            pocket.designationManager.AddDesignation(new Designation(b, vein));
            // Mining targets are cells, including cells without a loaded mineable
            // thing (such as plans under fog); no listerThings is required.
            foreach (var def in new[] { mine, vein })
            foreach (var cell in new[] { a, b })
                remote.designationManager.AddDesignation(new Designation(cell, def));
            var target = new Thing { def = new ThingDef { defName = "DesignationTarget" } };
            remote.designationManager.AddDesignation(new Designation(a, custom));
            remote.designationManager.AddDesignation(new Designation(target, hunt));
            foreach (var def in construction) remote.designationManager.AddDesignation(new Designation(target, def));

            Check(remote.designationManager.DesignationAt(a, mine) != null
                && remote.designationManager.DesignationAt(b, vein) != null, "Mining fixture was not indexed.");
            var drawCacheDirty = (DefMap<DesignationDef, bool>)AccessTools.Field(typeof(DesignationManager), "designationMatriciesDirty").GetValue(remote.designationManager)!;
            drawCacheDirty[mine] = drawCacheDirty[vein] = false;
            var result = RemoteSessionMapConstructionSanitizer.Apply(remote);
            Check(result.RemovedDesignations == 10 && result.RemovedPlans == 0 && result.RemovedPlaceholders == 0,
                "Remote cleanup did not remove all native and custom orders.");
            foreach (var def in new[] { mine, vein, custom })
            {
                Check(!remote.designationManager.SpawnedDesignationsOfDef(def).Any(), "Removed mining order remained in the work index.");
                Check(drawCacheDirty[def], "Removing mining orders did not invalidate their drawing cache.");
                foreach (var cell in new[] { a, b })
                    Check(remote.designationManager.DesignationAt(cell, def) is null, "Removed mining order remained in the cell index.");
            }
            Check(remote.designationManager.AllDesignations.Count == 0 && !target.Destroyed,
                "Cleanup retained a designation or destroyed its target.");
            foreach (var def in construction.Append(hunt))
                Check(remote.designationManager.DesignationOn(target, def) is null, "Designation remained in the thing index.");
            Check(local.designationManager.DesignationAt(a, mine) != null && pocket.designationManager.AllDesignations.Count == 2,
                "Root cleanup affected a different map.");
            Check(RemoteSessionMapConstructionSanitizer.Apply(remote).RemovedDesignations == 0, "Repeated designation cleanup was not idempotent.");
            Check(RemoteSessionMapConstructionSanitizer.Apply(pocket).RemovedDesignations == 2
                && pocket.designationManager.AllDesignations.Count == 0 && local.designationManager.DesignationAt(a, mine) != null,
                "Pocket cleanup retained mining orders or affected the local colony.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static DesignationDef Def(string name, TargetType targetType) => new() { defName = name, label = "本地化标签", targetType = targetType };
    private static Map Map(MapParent parent)
    {
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(10, 1, 10), parent = parent };
        map.cellIndices = new CellIndices(map);
        map.designationManager = new DesignationManager(map);
        return map;
    }
    private static bool Skip() => false;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
