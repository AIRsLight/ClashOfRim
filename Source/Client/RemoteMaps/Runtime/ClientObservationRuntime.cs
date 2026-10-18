using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal sealed class ClientObservationRuntime : IClientObservationRuntime
{
    public void EnqueueOpen(Action action, Action onDiscard) => ClientRemoteMapLongEvent.Enqueue(action, onDiscard);

    public string? Open(RemoteSessionMapOpenRequest request)
    {
        ClashLog.Message("[ClashOfRim][Observation] Opening " + request.Mode + " snapshot target="
            + request.Target.OwnerUserId + "/" + request.Target.OwnerColonyId + ", snapshot=" + request.SnapshotId);
        if (!RemoteSessionMapUtility.TryOpen(request, out var result)) return result.FailureReason;
        ClientRemoteSessionRuntime.RegisterOpenedObservation(request, result);
        return null;
    }
}
