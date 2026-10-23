using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.RemoteMaps;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class RemoteMapPlanTests
{
    private static Selector selector = null!;
    private static readonly List<IntVec3> dirty = new();

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.RemoteMapPlans");
        try
        {
            selector = new Selector();
            harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.Selector)), prefix: Method(nameof(Selector)));
            harmony.Patch(AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty), new[] { typeof(IntVec3), typeof(ulong) }), prefix: Method(nameof(Dirty)));
            harmony.Patch(AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), prefix: Method(nameof(Skip)));
            var local = Map(); var remote = Map(); var pocket = Map();
            var a = new IntVec3(1, 0, 1); var b = new IntVec3(2, 0, 1);
            var localPlan = Plan(local, a);
            var selected = Plan(remote, a, b);
            var hidden = Plan(remote, new IntVec3(3, 0, 1)); hidden.Hidden = true;
            Plan(remote); // Empty plans must deregister too.
            Plan(pocket, a);
            selector.SelectedObjects.Add(selected);
            dirty.Clear();

            var result = RemoteSessionMapConstructionSanitizer.Apply(remote);
            Check(result.RemovedPlans == 3 && result.RemovedPlaceholders == 0 && result.RemovedDesignations == 0,
                "Plan cleanup was tied to thing/designation managers or counted incorrectly.");
            Check(remote.planManager.AllPlans.Count == 0 && selected.CellCount == 0 && hidden.CellCount == 0
                && remote.planManager.PlanAt(a) == null && remote.planManager.PlanAt(b) == null
                && selector.SelectedObjects.Count == 0 && dirty.Count == 3,
                "Native plan cleanup left cells, selection or stale render meshes behind.");
            Check(RemoteSessionMapConstructionSanitizer.Apply(remote).RemovedPlans == 0, "Repeated cleanup was not idempotent.");
            Check(pocket.planManager.PlanAt(a) != null && local.planManager.PlanAt(a) == localPlan,
                "Root cleanup touched another map's plans.");
            Check(RemoteSessionMapConstructionSanitizer.Apply(pocket).RemovedPlans == 1
                && local.planManager.AllPlans.Single() == localPlan, "Pocket cleanup affected local plans.");
            Check(RemoteSessionMapConstructionSanitizer.Apply(null).RemovedPlans == 0, "Null map was not a no-op.");
        }
        finally { harmony.UnpatchAll(harmony.Id); dirty.Clear(); selector = null!; }
    }

    private static Map Map()
    {
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.cellIndices = new CellIndices(10, 10);
        map.mapDrawer = (MapDrawer)RuntimeHelpers.GetUninitializedObject(typeof(MapDrawer));
        map.planManager = new PlanManager(map);
        return map;
    }

    private static Plan Plan(Map map, params IntVec3[] cells)
    {
        var plan = new Plan { planManager = map.planManager };
        map.planManager.RegisterPlan(plan);
        foreach (var cell in cells) plan.AddCell(cell);
        return plan;
    }
    private static bool Selector(ref Selector __result) { __result = selector; return false; }
    private static bool Dirty(IntVec3 __0) { dirty.Add(__0); return false; }
    private static bool Skip() => false;
    private static HarmonyMethod Method(string name) => new(typeof(RemoteMapPlanTests), name);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
