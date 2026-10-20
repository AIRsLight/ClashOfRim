using System;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

[HarmonyPatch(typeof(PocketMapUtility), nameof(PocketMapUtility.GeneratePocketMap))]
internal static class RemoteSessionPocketMapGenerationPatch
{
    internal static void Postfix(Map __result)
        => RemoteSessionThingCleanup.MarkMapThingsForCleanup(__result,
            RemoteSessionPocketMapUtility.FindSessionParent(__result));
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
internal static class RemoteSessionPocketMapPawnPatch
{
    internal static void Postfix(Pawn __instance, Map map)
    {
        if (map.Parent is PocketMapParent)
            RemoteSessionThingCleanup.MarkThingForCleanup(__instance, RemoteSessionPocketMapUtility.FindSessionParent(map));
    }
}

[HarmonyPatch(typeof(PocketMapUtility), nameof(PocketMapUtility.DestroyPocketMap))]
internal static class RemoteSessionPocketMapRemovalPatch
{
    internal static void Prefix(Map map, out IDisposable? __state)
    {
        __state = null;
        var owner = RemoteSessionPocketMapUtility.FindSessionParent(map);
        if (owner is null || map.Parent is not PocketMapParent pocket) return;
        const string reason = "remote pocket map removal";
        RemoteSessionPocketMapUtility.ClosePocketMapsForSource(map, reason);
        RemoteSessionPocketMapUtility.PrepareCurrentMapForRemoval(map, pocket.sourceMap);
        RemoteSessionThingCleanup.MarkMapThingsForCleanup(map, owner);
        RemoteSessionThingCleanup.DiscardMarkedMapThings(map, owner, reason);
        __state = RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects();
    }

    internal static void Finalizer(Map map, IDisposable? __state, Exception? __exception)
    {
        if (__state is null) return;
        try
        {
            // Vanilla unregisters the parent before unloading. Retain a failed
            // unload in the tree so session close can retry it.
            if (__exception is not null && Current.Game.Maps.Contains(map)
                && map.Parent is PocketMapParent parent && !Find.World.pocketMaps.Contains(parent))
                Find.World.pocketMaps.Add(parent);
            if (__exception is null)
            {
                ClashOfRimGameComponent.RemoveRemoteMapThingIdentities(mapUniqueId: map.uniqueID.ToString());
                RemoteSessionPocketMapUtility.FindSessionParent(map)?.DeferredPockets.NotifyMapRemoved(map.GetUniqueLoadID());
            }
        }
        finally { __state.Dispose(); }
    }
}
