using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.Modules.Maps;

internal interface IClientRemoteSnapshotTransport
{
    Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context,
        ClientSnapshotDownloadTarget target, CancellationToken token, Action<string>? payloadStarting = null);
}
