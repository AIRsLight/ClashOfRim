using System;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidTrapVisibilitySessionRegistry
{
    private static RaidTrapVisibilityApplicationRequest? pendingRequest;
    private static Map? lastObservedMap;

    public static void RegisterPendingRequest(RaidTrapVisibilityApplicationRequest request)
    {
        pendingRequest = request;
        UpdateForCurrentMap();
    }

    public static void RegisterDelivery(RaidTrapVisibilityDelivery delivery)
    {
        if (delivery == null)
        {
            throw new ArgumentNullException(nameof(delivery));
        }

        RegisterPendingRequest(delivery.ToApplicationRequest());
    }

    public static void ClearPendingRequest()
    {
        pendingRequest = null;
        RaidTrapVisibilityController.EndHiddenTrapSession(Find.CurrentMap);
    }

    public static void UpdateForCurrentMap()
    {
        Map currentMap = Find.CurrentMap;
        if (currentMap != lastObservedMap)
        {
            if (lastObservedMap != null)
            {
                RaidTrapVisibilityController.EndHiddenTrapSession(lastObservedMap);
            }

            lastObservedMap = currentMap;
        }

        if (pendingRequest == null)
        {
            RaidTrapVisibilityController.EndHiddenTrapSession(currentMap);
            return;
        }

        if (currentMap == null)
        {
            RaidTrapVisibilityController.EndHiddenTrapSession();
            return;
        }

        if (IsCurrentRequestApplied(currentMap))
        {
            return;
        }

        RaidTrapVisibilityController.ApplyHiddenTrapSession(pendingRequest, currentMap);
    }

    private static bool IsCurrentRequestApplied(Map currentMap)
    {
        if (!RaidTrapVisibilityController.Active || pendingRequest == null)
        {
            return false;
        }

        return string.Equals(RaidTrapVisibilityController.CurrentRaidEventId, pendingRequest.RaidEventId, StringComparison.Ordinal) &&
            string.Equals(RaidTrapVisibilityController.CurrentTargetSnapshotId, pendingRequest.TargetSnapshotId, StringComparison.Ordinal) &&
            string.Equals(RaidTrapVisibilityController.CurrentTargetMapLoadId, currentMap.GetUniqueLoadID(), StringComparison.Ordinal);
    }
}
