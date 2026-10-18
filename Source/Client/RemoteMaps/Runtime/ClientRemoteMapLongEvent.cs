using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

internal static class ClientRemoteMapLongEvent
{
    internal static void Enqueue(Action action, Action discarded, string textKey = "ClashOfRim.RemoteSessionMap.StatusGeneratingLong") =>
        LongEventHandler.QueueLongEvent(ClientSessionWork.Guard(action, discarded), textKey, doAsynchronously: false,
            exceptionHandler: ex => Log.Warning("[ClashOfRim] Remote map long event failed: " + ex), showExtraUIInfo: false);
}
