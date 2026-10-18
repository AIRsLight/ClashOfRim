using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal sealed class ClientObservationRuntime : IClientObservationRuntime
{
    public void EnqueueOpen(Action action, Action onDiscard) => LongEventHandler.QueueLongEvent(
        ClientSessionWork.Guard(action, onDiscard), "ClashOfRim.RemoteSessionMap.StatusGeneratingLong", doAsynchronously: false,
        exceptionHandler: ex => Log.Warning("[ClashOfRim] Remote map long event failed: " + ex), showExtraUIInfo: false);

    public string? Open(RemoteSessionMapOpenRequest request)
    {
        ClashLog.Message("[ClashOfRim][Observation] Opening " + request.Mode + " snapshot target="
            + request.Target.OwnerUserId + "/" + request.Target.OwnerColonyId + ", snapshot=" + request.SnapshotId);
        if (!RemoteSessionMapUtility.TryOpen(request, out var result)) return result.FailureReason;
        RemoteMapSessionController.RegisterOpenedObservation(request, result);
        return null;
    }
}
