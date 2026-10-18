using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.RemoteMaps;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientObservationTransport : IClientObservationTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        CancellationToken token, Action<ModLoginResponseDto> captureReceipt)
    {
        using var http = CreateHttp();
        var environment = new ClientAuthenticationEnvironment();
        return await new ClientSessionProtocol(Protocol(http, context), environment, environment)
            .LoginAsync(context, "observation", null, token, captureReceipt).ConfigureAwait(false);
    }
    public async Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context,
        ClientSnapshotDownloadTarget target, CancellationToken token)
    {
        using var http = CreateHttp();
        var protocol = new ClientSnapshotDownloadProtocol(Protocol(http, context), context,
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await new ClientRemoteSnapshotDownload(protocol).DownloadAsync(target, token).ConfigureAwait(false);
    }
    private static HttpClient CreateHttp() => new() { Timeout = TimeSpan.FromSeconds(60) };
    private static ClientProtocolTransport Protocol(HttpClient http, ClashOfRimClientNetworkContext context) =>
        new(http, context, new ClientProtocolEnvironment((message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner)));
}
