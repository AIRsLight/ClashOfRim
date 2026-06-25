using System;
using System.Linq;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionPocketMapUtility
{
    public static bool IsRemoteSessionSourceMap(Map? map)
    {
        return map?.Parent is RemoteSessionMapParent;
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
                PrepareCurrentMapForRemoval(pocketMap, sourceMap);
                RemoteSessionThingCleanup.DiscardMarkedMapThings(
                    pocketMap,
                    sourceMap.Parent as RemoteSessionMapParent,
                    reason);
                using (RemoteSessionGlobalStateGuard.BeginSuppressRemoteMapRemovalGlobalEffects())
                {
                    Current.Game.DeinitAndRemoveMap(pocketMap, notifyPlayer: false);
                }
            }

            if (Find.World.pocketMaps.Contains(pocketMapParent))
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
                + ".");
        }

        return closed;
    }

    private static void PrepareCurrentMapForRemoval(Map map, Map sourceMap)
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

        Map? fallback = Current.Game.Maps.FirstOrDefault(candidate => !ReferenceEquals(candidate, map));
        if (fallback is not null)
        {
            Current.Game.CurrentMap = fallback;
            CameraJumper.TryJump(fallback.Center, fallback, CameraJumper.MovementMode.Cut);
        }
    }
}
