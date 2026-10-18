using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientRemoteSnapshotTransport : IClientRemoteSnapshotTransport
{
    public async Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context,
        ClientSnapshotDownloadTarget target, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var transport = new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(
            (message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner)));
        var protocol = new ClientSnapshotDownloadProtocol(transport, context,
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await new ClientRemoteSnapshotDownload(protocol).DownloadAsync(target, token).ConfigureAwait(false);
    }
}
