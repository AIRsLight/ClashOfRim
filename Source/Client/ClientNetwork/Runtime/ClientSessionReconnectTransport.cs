using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionReconnectTransport : IClientSessionReconnectTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        return await new ClashOfRimModNetworkClient(http, context).PrepareWorldSessionAsync(cancellationToken: token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        var environment = new ClientAuthenticationEnvironment();
        var transport = new ClientProtocolTransport(http, context,
            new ClientProtocolEnvironment((message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner)));
        return await new ClientSessionProtocol(transport, environment, environment).LoginAsync(context, "session-reconnect", null, token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> ReadSnapshotAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = CreateHttp();
        return await new ClashOfRimModNetworkClient(http, context).DownloadLatestSnapshotAsync(cancellationToken: token);
    }

    private static HttpClient CreateHttp() => new() { Timeout = TimeSpan.FromSeconds(30) };
}
