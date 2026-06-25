using System;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionMapAreaSanitizer
{
    public static RemoteSessionMapAreaSanitizerResult Apply(Map? map)
    {
        if (map is null)
        {
            return RemoteSessionMapAreaSanitizerResult.Empty;
        }

        int removedZones = RemoveZones(map);
        int clearedAreas = ClearAreas(map);

        if (removedZones > 0 || clearedAreas > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteSession] Cleared remote map work areas: map="
                + map.GetUniqueLoadID()
                + ", zones="
                + removedZones
                + ", areas="
                + clearedAreas
                + ".");
        }

        return new RemoteSessionMapAreaSanitizerResult(removedZones, clearedAreas);
    }

    private static int RemoveZones(Map map)
    {
        if (map.zoneManager?.AllZones is null)
        {
            return 0;
        }

        int removed = 0;
        foreach (Zone zone in map.zoneManager.AllZones.ToList())
        {
            try
            {
                zone.Delete();
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to delete remote map zone "
                    + zone.GetType().Name
                    + ": "
                    + ex);
            }
        }

        return removed;
    }

    private static int ClearAreas(Map map)
    {
        if (map.areaManager?.AllAreas is null)
        {
            return 0;
        }

        int cleared = 0;
        foreach (Area area in map.areaManager.AllAreas.ToList())
        {
            try
            {
                if (!area.ActiveCells.Any())
                {
                    continue;
                }

                area.Clear();
                cleared++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to clear remote map area "
                    + area.GetType().Name
                    + ": "
                    + ex);
            }
        }

        return cleared;
    }
}

public readonly struct RemoteSessionMapAreaSanitizerResult
{
    public static readonly RemoteSessionMapAreaSanitizerResult Empty = new(0, 0);

    public RemoteSessionMapAreaSanitizerResult(int removedZones, int clearedAreas)
    {
        RemovedZones = removedZones;
        ClearedAreas = clearedAreas;
    }

    public int RemovedZones { get; }

    public int ClearedAreas { get; }
}
