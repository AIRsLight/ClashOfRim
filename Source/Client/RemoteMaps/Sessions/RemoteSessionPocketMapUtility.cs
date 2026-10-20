using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionPocketMapUtility
{
    public static bool IsRemoteSessionSourceMap(Map? map)
    {
        return FindSessionParent(map) is not null;
    }

    public static Map? RootMap(Map? map)
    {
        while (map?.Parent is PocketMapParent { sourceMap: not null } pocket)
            map = pocket.sourceMap;
        return map;
    }

    public static RemoteSessionMapParent? FindSessionParent(Map? map)
        => RootMap(map)?.Parent as RemoteSessionMapParent;

    public static IEnumerable<Map> MapTree(Map map)
    {
        yield return map;
        foreach (Map child in map.ChildPocketMaps)
        {
            if (child is null) continue;
            foreach (Map descendant in MapTree(child)) yield return descendant;
        }
    }

    public static bool IsRemoteSessionPocketMap(Map? map)
    {
        return map?.Parent is PocketMapParent pocketMapParent
            && IsRemoteSessionSourceMap(pocketMapParent.sourceMap);
    }

    public static int ClosePocketMapsForSource(Map? sourceMap, string reason)
    {
        if (sourceMap is null || Find.World?.pocketMaps is null || Current.Game is null)
        {
            return 0;
        }

        var pocketMapParents = Find.World.pocketMaps
            .Where(parent => ReferenceEquals(parent.sourceMap, sourceMap))
            .ToList();
        if (pocketMapParents.Count == 0)
        {
            return 0;
        }

        int closed = 0;
        foreach (PocketMapParent pocketMapParent in pocketMapParents)
        {
            Map? pocketMap = pocketMapParent.Map;
            if (pocketMap is not null && Current.Game.Maps.Contains(pocketMap))
            {
                // The native removal patch applies the same cleanup for a collapse
                // and for closing the owning remote session, including descendants.
                PocketMapUtility.DestroyPocketMap(pocketMap);
            }
            else
            {
                Find.World.pocketMaps.Remove(pocketMapParent);
            }

            closed++;
        }

        if (closed > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteMap] Closed remote child pocket maps: count="
                + closed
                + ", source=Map_"
                + sourceMap.uniqueID
                + ", reason=" + reason
                + ".");
        }

        return closed;
    }

    internal static void PrepareCurrentMapForRemoval(Map map, Map sourceMap)
    {
        if (!ReferenceEquals(Current.Game?.CurrentMap, map))
        {
            return;
        }

        if (Current.Game.Maps.Contains(sourceMap))
        {
            Current.Game.CurrentMap = sourceMap;
            CameraJumper.TryJump(sourceMap.Center, sourceMap, CameraJumper.MovementMode.Cut);
            return;
        }

        RemoteSessionMapUtility.PrepareCurrentMapForRemoval(map);
    }
}
